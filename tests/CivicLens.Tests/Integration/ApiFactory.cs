using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CivicLens.Application.Demo;
using CivicLens.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CivicLens.Tests.Integration;

/// <summary>Boots the real API (DEMO_MODE seeding, auth, filters) on an EF InMemory database.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Guid.NewGuid().ToString();
    private readonly string _uploads = Path.Combine(Path.GetTempPath(), "civiclens-tests-" + Guid.NewGuid().ToString("N"));
    public int MaxObservationsPerHour { get; init; } = 1000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Storage:Path", _uploads);
        builder.UseSetting("RateLimit:AuthPerMinute", "10000");
        builder.UseSetting("RateLimit:UploadsPerMinute", "10000");
        builder.UseSetting("RateLimit:PerMinute", "100000");
        builder.UseSetting("CivicLens:Scoring:MaxObservationsPerHour", MaxObservationsPerHour.ToString());
        builder.ConfigureServices(s =>
        {
            s.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            s.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_db));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(_uploads)) Directory.Delete(_uploads, true); } catch (IOException) { }
    }

    public ApiClient Client() => new(CreateClient());

    public async Task<ApiClient> LoginAsync(string email, string password = DemoCity.DemoPassword)
    {
        var c = Client();
        await c.LoginAsync(email, password);
        return c;
    }
}

public sealed record ErrorInfo(string Code, string Message);

public sealed class ApiException(HttpStatusCode status, string code, string message) : Exception($"{(int)status} {code}: {message}")
{
    public string Detail { get; } = message;
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Thin typed client that unwraps the { success, data, error, correlationId } envelope.</summary>
public sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    public HttpClient Http => http;
    public string? LastBody { get; private set; }

    public async Task LoginAsync(string email, string password)
    {
        var res = await PostAsync<CivicLens.Application.Dtos.AuthResponse>("/api/auth/login", new { email, password });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.Token);
    }

    public Task<T> GetAsync<T>(string url) => SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, url));

    public Task<T> PostAsync<T>(string url, object? body = null) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }, options: J) });

    public Task<T> PostFormAsync<T>(string url, MultipartFormDataContent form) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Post, url) { Content = form });

    public async Task<ApiException> ExpectErrorAsync(Func<Task> action, HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, ex.Status);
        return ex;
    }

    public async Task<T> SendAsync<T>(HttpRequestMessage req)
    {
        var res = await http.SendAsync(req);
        LastBody = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(LastBody);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("success", out _), $"Response is not an envelope: {LastBody}");
        Assert.True(root.TryGetProperty("correlationId", out var cid) && !string.IsNullOrEmpty(cid.GetString()));
        if (!root.GetProperty("success").GetBoolean() || !res.IsSuccessStatusCode)
        {
            var err = root.GetProperty("error");
            throw new ApiException(res.StatusCode, err.GetProperty("code").GetString()!, err.GetProperty("message").GetString()!);
        }
        return root.GetProperty("data").Deserialize<T>(J)!;
    }

    // ---- multipart helpers
    public static MultipartFormDataContent PhotoForm(string fileName, byte[] bytes, double lat, double lon, string? description = null,
        double? deviceLat = null, double? deviceLon = null, string contentType = "image/jpeg")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "image", fileName);
        form.Add(new StringContent(lat.ToString(System.Globalization.CultureInfo.InvariantCulture)), "latitude");
        form.Add(new StringContent(lon.ToString(System.Globalization.CultureInfo.InvariantCulture)), "longitude");
        if (description is not null) form.Add(new StringContent(description), "description");
        if (deviceLat is not null) form.Add(new StringContent(deviceLat.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "deviceLatitude");
        if (deviceLon is not null) form.Add(new StringContent(deviceLon.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "deviceLongitude");
        return form;
    }

    public static MultipartFormDataContent AfterPhotoForm(string fileName, string? notes = "Work finished")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(DemoImages.Bytes(Path.GetFileNameWithoutExtension(fileName)));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "afterImage", fileName);
        if (notes is not null) form.Add(new StringContent(notes), "notes");
        return form;
    }
}
