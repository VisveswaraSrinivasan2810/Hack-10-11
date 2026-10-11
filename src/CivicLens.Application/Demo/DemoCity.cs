using CivicLens.Domain;

namespace CivicLens.Application.Demo;

/// <summary>Fictional Hyderabad demonstration data, not verified real wards or landmarks. All coordinates are shared by the seeder and the scenario runner.</summary>
public static class DemoCity
{
    public const string Name = "Hyderabad";
    public const double MapMinLat = 17.3784, MapMaxLat = 17.3924;
    public const double MapMinLon = 78.4721, MapMaxLon = 78.5021;

    public const double CenterLat = 17.3850;
    public const double CenterLon = 78.4867;

    public const string CitizenEmail = "citizen@civiclens.demo";
    public const string CitizenTwoEmail = "citizen2@civiclens.demo";
    public const string AuthorityEmail = "authority@civiclens.demo";
    public const string WorkerEmail = "worker@civiclens.demo";
    public const string DemoPassword = "Demo@12345";

    public static readonly (string Name, double MinLat, double MaxLat, double MinLon, double MaxLon)[] Zones =
    [
        ("Zone A — Lakdikapul (demo)", 17.3784, 17.3924, 78.4721, 78.4821),
        ("Zone B — Abids (demo)", 17.3784, 17.3924, 78.4821, 78.4921),
        ("Zone C — Koti (demo)", 17.3784, 17.3924, 78.4921, 78.5021),
    ];

    public static readonly (string Name, PoiType Type, double Lat, double Lon, double Weight)[] Pois =
    [
        ("Abids Demo Public School", PoiType.School, 17.3846, 78.4861, 100),
        ("Abids Demo Hospital", PoiType.Hospital, 17.3879, 78.4886, 100),
        ("Abids Demo Bus Stop", PoiType.BusStop, 17.3834, 78.4876, 70),
        ("Abids Demo Junction", PoiType.Junction, 17.3864, 78.4906, 80),
        ("Koti Demo Market", PoiType.Market, 17.3824, 78.4961, 85),
        ("Lakdikapul Demo Primary School", PoiType.School, 17.3834, 78.4751, 100),
    ];

    // Main jury story: pothole 72 m north of Abids Demo Public School.
    public const double SchoolRoadLat = 17.38525, SchoolRoadLon = 78.4861;
    // Scenario spots (kept away from seeded incidents so that live demos create fresh incidents).
    public const double QuietRoadLat = 17.3794, QuietRoadLon = 78.4781;       // quiet street, Zone A
    public const double DuplicateLat = 17.3799, DuplicateLon = 78.4811;      // scenario 2 (kept apart from scenario 1)
    public const double LakdikapulSchoolLat = 17.38395, LakdikapulSchoolLon = 78.4751; // ~60 m from Lakdikapul Primary
    public const double GarbageLat = 17.3909, GarbageLon = 78.4991;           // Zone C
    public const double WaterLat = 17.3804, WaterLon = 78.5001;               // Zone C
    public const double RecurringXLat = 17.3835, RecurringXLon = 78.4951;     // Location X (3 resolved incidents)
    public const double LeakCorrLat = 17.3811, LeakCorrLon = 78.4801;         // scenario 9 base leak (zone A)
    public const double HotspotLat = 17.3864, HotspotLon = 78.4906;           // Abids Demo Junction cluster
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
