using CivicLens.Domain;

namespace CivicLens.Application.Demo;

/// <summary>Fictional demonstration city. All coordinates are shared by the seeder and the scenario runner.</summary>
public static class DemoCity
{
    public const double CenterLat = 12.9716;
    public const double CenterLon = 77.5946;

    public const string CitizenEmail = "citizen@civiclens.demo";
    public const string CitizenTwoEmail = "citizen2@civiclens.demo";
    public const string AuthorityEmail = "authority@civiclens.demo";
    public const string WorkerEmail = "worker@civiclens.demo";
    public const string DemoPassword = "Demo@12345";

    public static readonly (string Name, double MinLat, double MaxLat, double MinLon, double MaxLon)[] Zones =
    [
        ("Zone A — Lakeview", 12.9650, 12.9790, 77.5800, 77.5900),
        ("Zone B — Central", 12.9650, 12.9790, 77.5900, 77.6000),
        ("Zone C — Old Town", 12.9650, 12.9790, 77.6000, 77.6100),
    ];

    public static readonly (string Name, PoiType Type, double Lat, double Lon, double Weight)[] Pois =
    [
        ("Demo Public School", PoiType.School, 12.9712, 77.5940, 100),
        ("City General Hospital", PoiType.Hospital, 12.9745, 77.5965, 100),
        ("Central Bus Stop", PoiType.BusStop, 12.9700, 77.5955, 70),
        ("Demo Junction", PoiType.Junction, 12.9730, 77.5985, 80),
        ("Old Town Market", PoiType.Market, 12.9690, 77.6040, 85),
        ("Lakeview Primary School", PoiType.School, 12.9700, 77.5830, 100),
    ];

    // Main jury story: pothole 72 m north of Demo Public School.
    public const double SchoolRoadLat = 12.97185, SchoolRoadLon = 77.5940;
    // Scenario spots (kept away from seeded incidents so that live demos create fresh incidents).
    public const double QuietRoadLat = 12.9660, QuietRoadLon = 77.5860;       // quiet street, Zone A
    public const double DuplicateLat = 12.9665, DuplicateLon = 77.5890;      // scenario 2 (kept apart from scenario 1)
    public const double LakeviewSchoolLat = 12.97055, LakeviewSchoolLon = 77.5830; // ~60 m from Lakeview Primary
    public const double GarbageLat = 12.9775, GarbageLon = 77.6070;           // Zone C
    public const double WaterLat = 12.9670, WaterLon = 77.6080;               // Zone C
    public const double RecurringXLat = 12.9701, RecurringXLon = 77.6030;     // Location X (3 resolved incidents)
    public const double LeakCorrLat = 12.9677, LeakCorrLon = 77.5880;         // scenario 9 base leak (zone A)
    public const double HotspotLat = 12.9730, HotspotLon = 77.5985;           // Demo Junction cluster
}

/// <summary>Builds synthetic image uploads for seeding/scenarios. The file name carries a hint for the demo vision provider.</summary>
public static class DemoImages
{
    public static byte[] Bytes(string hint)
    {
        var rnd = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096);
        return [0xFF, 0xD8, 0xFF, 0xE0, .. System.Text.Encoding.ASCII.GetBytes(hint), .. rnd, 0xFF, 0xD9];
    }

    public static Abstractions.ImageUpload Upload(string hint) => new(Bytes(hint), $"{hint}_{Guid.NewGuid():N}.jpg", "image/jpeg");

    /// <summary>Static illustration shipped with the API (wwwroot/demo) used for seeded incidents.</summary>
    public static string SeedUrl(IssueCategory c) => $"/demo/{c.ToString().ToLowerInvariant()}.svg";
    public const string FixedUrl = "/demo/fixed.svg";
}
