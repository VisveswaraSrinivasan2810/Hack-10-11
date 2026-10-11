using System.Globalization;
using System.Text;
using System.Text.Json;
using CivicLens.Application.Abstractions;
using CivicLens.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CivicLens.Infrastructure.Services;

public sealed class GeocodingOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://nominatim.openstreetmap.org";
    /// <summary>Nominatim's usage policy requires an identifying User-Agent. Set Geocoding__UserAgent to your app + contact.</summary>
    public string UserAgent { get; set; } = "CivicLensAI/1.0 (set Geocoding__UserAgent with a contact)";
    public int TimeoutSeconds { get; set; } = 4;
}

public sealed class PoiOptions
{
    public string OverpassUrl { get; set; } = "https://overpass-api.de/api/interpreter";
    public string UserAgent { get; set; } = "CivicLensAI/1.0 (set Poi__UserAgent with a contact)";
    public int TimeoutSeconds { get; set; } = 12;
}

/// <summary>Reverse geocoding through Nominatim. Never throws: a failure just means the incident has no street label.</summary>
public sealed class NominatimGeocoder(HttpClient http, IOptions<GeocodingOptions> options, ILogger<NominatimGeocoder> logger) : IGeocoder
{
    private readonly GeocodingOptions _o = options.Value;

    public async Task<string?> ReverseAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        if (!_o.Enabled) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{_o.BaseUrl.TrimEnd('/')}/reverse?format=jsonv2&zoom=17&addressdetails=1&lat={latitude}&lon={longitude}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(_o.UserAgent);
            using var res = await http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            if (!doc.RootElement.TryGetProperty("address", out var a)) return null;

            string? Pick(params string[] keys) => keys.Select(k => a.TryGetProperty(k, out var v) ? v.GetString() : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            var road = Pick("road", "pedestrian", "footway", "path", "residential");
            var area = Pick("suburb", "neighbourhood", "city_district", "quarter", "village", "town", "city");
            var label = string.Join(", ", new[] { road, area }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return label.Length == 0 ? null : label[..Math.Min(120, label.Length)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Reverse geocoding failed");
            return null;
        }
    }
}

/// <summary>Imports schools, hospitals, markets, bus stops and signalised junctions from OpenStreetMap (Overpass API).</summary>
public sealed class OverpassPoiImporter(HttpClient http, IOptions<PoiOptions> options, ILogger<OverpassPoiImporter> logger) : IPoiImporter
{
    private readonly PoiOptions _o = options.Value;

    public async Task<IReadOnlyList<ImportedPoi>> FindAroundAsync(double latitude, double longitude, double radiusMeters, CancellationToken ct = default)
    {
        var r = Math.Clamp((int)radiusMeters, 100, 5000);
        var at = string.Create(CultureInfo.InvariantCulture, $"{r},{latitude},{longitude}");
        var query = $"[out:json][timeout:10];(" +
                    $"nwr(around:{at})[amenity~\"^(school|hospital|marketplace)$\"];" +
                    $"node(around:{at})[highway=bus_stop];" +
                    $"node(around:{at})[highway=traffic_signals];" +
                    $");out center 400;";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
            using var req = new HttpRequestMessage(HttpMethod.Post, _o.OverpassUrl)
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query }) };
            req.Headers.UserAgent.ParseAdd(_o.UserAgent);
            using var res = await http.SendAsync(req, cts.Token);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));

            var list = new List<ImportedPoi>();
            foreach (var e in doc.RootElement.GetProperty("elements").EnumerateArray())
            {
                if (!e.TryGetProperty("tags", out var tags)) continue;
                double lat, lon;
                if (e.TryGetProperty("lat", out var la) && e.TryGetProperty("lon", out var lo)) { lat = la.GetDouble(); lon = lo.GetDouble(); }
                else if (e.TryGetProperty("center", out var c)) { lat = c.GetProperty("lat").GetDouble(); lon = c.GetProperty("lon").GetDouble(); }
                else continue;

                string? Tag(string k) => tags.TryGetProperty(k, out var v) ? v.GetString() : null;
                var amenity = Tag("amenity"); var highway = Tag("highway");
                PoiType? type = amenity switch { "school" => PoiType.School, "hospital" => PoiType.Hospital, "marketplace" => PoiType.Market, _ => null };
                type ??= highway == "bus_stop" ? PoiType.BusStop : highway == "traffic_signals" ? PoiType.Junction : null;
                if (type is null) continue;
                var name = Tag("name") ?? type switch
                {
                    PoiType.School => "School", PoiType.Hospital => "Hospital", PoiType.Market => "Market",
                    PoiType.BusStop => "Bus stop", _ => "Signalised junction"
                };
                list.Add(new ImportedPoi(name[..Math.Min(120, name.Length)], type.Value, lat, lon));
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "POI import from Overpass failed");
            return [];
        }
    }
}
