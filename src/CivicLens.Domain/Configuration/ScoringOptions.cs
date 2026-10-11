namespace CivicLens.Domain.Configuration;

/// <summary>All tunable numbers of the intelligence layer. Bound from the "CivicLens" config section.</summary>
public class ScoringOptions
{
    // CPS weights (must sum to 1)
    public double WeightSeverity { get; set; } = 0.25;
    public double WeightCitizenEvidence { get; set; } = 0.15;
    public double WeightLocationRisk { get; set; } = 0.20;
    public double WeightIncidentAge { get; set; } = 0.10;
    public double WeightExposure { get; set; } = 0.15;
    public double WeightRecurrence { get; set; } = 0.15;
    /// <summary>
    /// Context amplifier: weighted sum is multiplied by (1 + max * clamp((LocationRisk-50)/50, 0..1)).
    /// Needed because with age = 0 and recurrence = 0 the plain weighted formula tops out at 75, so a brand-new incident next to a school
    /// could never be CRITICAL. Set to 0 to use the plain specification formula.
    /// </summary>
    public double ContextAmplifierMax { get; set; } = 0.75;

    // Same-incident fingerprint
    public double MatchRadiusMeters { get; set; } = 50;
    public double WeightSpatial { get; set; } = 0.40;
    public double WeightCategory { get; set; } = 0.30;
    public double WeightVisual { get; set; } = 0.20;
    public double WeightTemporal { get; set; } = 0.10;
    public double VeryLikelyThreshold { get; set; } = 0.85;
    public double PossibleThreshold { get; set; } = 0.65;
    /// <summary>Recently resolved incidents stay candidates this long (attaching reopens them).</summary>
    public int ResolvedCandidateDays { get; set; } = 14;

    // Recurrence
    public double RecurrenceRadiusMeters { get; set; } = 50;
    public int RecurrenceWindowDays { get; set; } = 365;
    public int RecurringMinPriorIncidents { get; set; } = 2;

    // Correlation
    public double CorrelationRadiusMeters { get; set; } = 50;
    public int CorrelationWindowDays { get; set; } = 30;

    // AI thresholds
    public double MinClassificationConfidence { get; set; } = 0.60;
    public double ResolutionVerifiedThreshold { get; set; } = 0.75;
    public double ResolutionDisputedThreshold { get; set; } = 0.50;
    public double AiWeightInResolution { get; set; } = 0.60;
    public int MinCitizenVotesForVerification { get; set; } = 1;

    // Abuse protection
    public int MaxObservationsPerHour { get; set; } = 10;
    public double MaxDeviceDistanceMeters { get; set; } = 2000;
    public double ServiceAreaCenterLat { get; set; } = 12.9716;
    public double ServiceAreaCenterLon { get; set; } = 77.5946;
    public double ServiceAreaRadiusKm { get; set; } = 25;

    // Hotspots
    public double HotspotRadiusMeters { get; set; } = 150;
    public int HotspotMinIncidents { get; set; } = 3;
}
