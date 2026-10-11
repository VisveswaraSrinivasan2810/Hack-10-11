namespace CivicLens.Domain.Scoring;

public static class CategoryRules
{
    public static string Family(IssueCategory c) => c switch
    {
        IssueCategory.Pothole or IssueCategory.RoadDamage => "RoadSurface",
        IssueCategory.GarbageAccumulation or IssueCategory.IllegalDumping => "Waste",
        _ => c.ToString()
    };

    /// <summary>1 = same category, 0.7 = same family (pothole vs road damage), 0 = unrelated.</summary>
    public static double Similarity(IssueCategory a, IssueCategory b)
    {
        if (a == b) return 1.0;
        return Family(a) == Family(b) ? 0.7 : 0.0;
    }

    public static string Label(IssueCategory c) => c switch
    {
        IssueCategory.Pothole => "Pothole",
        IssueCategory.GarbageAccumulation => "Garbage Accumulation",
        IssueCategory.WaterLeakage => "Water Leakage",
        IssueCategory.SewageOverflow => "Sewage Overflow",
        IssueCategory.BrokenStreetlight => "Broken Streetlight",
        IssueCategory.RoadDamage => "Road Damage",
        IssueCategory.DamagedFootpath => "Damaged Footpath",
        IssueCategory.FallenTree => "Fallen Tree",
        IssueCategory.IllegalDumping => "Illegal Dumping",
        IssueCategory.DamagedPublicInfrastructure => "Damaged Public Infrastructure",
        IssueCategory.NoIssue => "No issue detected",
        _ => "Other"
    };

    /// <summary>Baseline severity (0-100) used by the demo classifier and as a floor for exposure.</summary>
    public static int BaseSeverity(IssueCategory c) => c switch
    {
        IssueCategory.Pothole => 70,
        IssueCategory.RoadDamage => 72,
        IssueCategory.SewageOverflow => 78,
        IssueCategory.FallenTree => 80,
        IssueCategory.WaterLeakage => 60,
        IssueCategory.BrokenStreetlight => 55,
        IssueCategory.DamagedFootpath => 50,
        IssueCategory.DamagedPublicInfrastructure => 58,
        IssueCategory.GarbageAccumulation => 45,
        IssueCategory.IllegalDumping => 48,
        _ => 30
    };

    public static double BaseExposure(IssueCategory c) => c switch
    {
        IssueCategory.Pothole => 35,
        IssueCategory.RoadDamage => 40,
        IssueCategory.DamagedFootpath => 35,
        IssueCategory.BrokenStreetlight => 30,
        IssueCategory.GarbageAccumulation => 25,
        IssueCategory.IllegalDumping => 20,
        IssueCategory.WaterLeakage => 30,
        IssueCategory.SewageOverflow => 40,
        IssueCategory.FallenTree => 45,
        IssueCategory.DamagedPublicInfrastructure => 30,
        _ => 20
    };
}
