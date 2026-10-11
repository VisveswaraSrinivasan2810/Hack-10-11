using CivicLens.Domain.Configuration;

namespace CivicLens.Domain.Scoring;

public sealed record CpsInput(
    IssueCategory Category,
    double Severity,
    double EffectiveObservations,
    double LocationRisk,
    double AgeDays,
    double Exposure,
    int RecurrencePriorCount,
    IReadOnlyList<string>? ContextReasons = null);

public sealed record CpsResult(
    double Severity, double CitizenEvidence, double LocationRisk, double IncidentAge, double Exposure, double Recurrence,
    double FinalScore, PriorityBand Band, IReadOnlyList<string> Reasons);

/// <summary>Civic Priority Score — transparent 0..100 composite with human-readable reasons.</summary>
public static class CpsCalculator
{
    public static PriorityBand BandFor(double score) => score switch
    {
        >= 85 => PriorityBand.Critical,
        >= 70 => PriorityBand.VeryHigh,
        >= 50 => PriorityBand.High,
        >= 30 => PriorityBand.Moderate,
        _ => PriorityBand.Low
    };

    public static string BandLabel(PriorityBand b) => b switch
    {
        PriorityBand.Critical => "CRITICAL",
        PriorityBand.VeryHigh => "VERY HIGH",
        PriorityBand.High => "HIGH",
        PriorityBand.Moderate => "MODERATE",
        _ => "LOW"
    };

    /// <summary>Evidence saturates: 1 obs ≈ 33, 2 ≈ 55, 3 ≈ 70, 5 ≈ 86, 7 ≈ 94.</summary>
    public static double EvidenceScore(double effectiveObservations) =>
        Math.Round(100 * (1 - Math.Exp(-0.4 * Math.Max(0, effectiveObservations))), 1);

    public static double AgeScore(double ageDays) => Math.Round(100 * (1 - Math.Exp(-Math.Max(0, ageDays) / 14.0)), 1);

    public static CpsResult Calculate(CpsInput i, ScoringOptions o)
    {
        var severity = Math.Clamp(i.Severity, 0, 100);
        var evidence = EvidenceScore(i.EffectiveObservations);
        var risk = Math.Clamp(i.LocationRisk, 0, 100);
        var age = AgeScore(i.AgeDays);
        var exposure = Math.Clamp(i.Exposure, 0, 100);
        var recurrence = RecurrenceEvaluator.ScoreFor(i.RecurrencePriorCount);

        var final = severity * o.WeightSeverity + evidence * o.WeightCitizenEvidence + risk * o.WeightLocationRisk
                    + age * o.WeightIncidentAge + exposure * o.WeightExposure + recurrence * o.WeightRecurrence;
        final = Math.Round(Math.Clamp(final, 0, 100), 1);
        var band = BandFor(final);

        var reasons = new List<string>();
        if (severity >= 65) reasons.Add($"Dangerous {CategoryRules.Label(i.Category).ToLowerInvariant()} detected (severity {severity:0})");
        else reasons.Add($"{CategoryRules.Label(i.Category)} with {(severity >= 40 ? "moderate" : "low")} severity ({severity:0})");
        if (i.EffectiveObservations >= 1.5) reasons.Add($"{Math.Round(i.EffectiveObservations)} independent citizen observations/confirmations");
        if (i.ContextReasons is { Count: > 0 }) reasons.AddRange(i.ContextReasons);
        else if (risk < 50) reasons.Add("Located in a low-risk area");
        if (exposure >= 60) reasons.Add($"High public exposure ({exposure:0})");
        if (age >= 40) reasons.Add($"Unresolved for {i.AgeDays:0} days");
        if (i.RecurrencePriorCount >= 1) reasons.Add($"Recurring issue: {i.RecurrencePriorCount} earlier related incident(s) at this location");

        return new CpsResult(severity, evidence, risk, age, exposure, recurrence, final, band, reasons);
    }
}
