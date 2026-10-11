using CivicLens.Domain.Configuration;

namespace CivicLens.Domain.Scoring;

public sealed record PriorIncident(Guid Id, string Code, IssueCategory Category, DateTime OccurredAt, double DistanceMeters);

public sealed record RecurrenceResult(
    int PriorCount, int RelatedTotal, int WindowDays, bool IsRecurring, RecurrenceRisk Risk,
    double Score, IReadOnlyList<PriorIncident> Priors, string Summary);

/// <summary>
/// Recurrence intelligence: how often has this physical location had a related issue
/// (same category family) within the look-back window?
/// </summary>
public static class RecurrenceEvaluator
{
    public static double ScoreFor(int priorCount) => priorCount switch { <= 0 => 0, 1 => 35, 2 => 60, 3 => 80, _ => 95 };

    public static RecurrenceResult Evaluate(
        IssueCategory category, DateTime now, IEnumerable<PriorIncident> resolvedHistory, ScoringOptions o)
    {
        var cutoff = now.AddDays(-o.RecurrenceWindowDays);
        var priors = resolvedHistory
            .Where(p => p.DistanceMeters <= o.RecurrenceRadiusMeters
                        && p.OccurredAt >= cutoff && p.OccurredAt <= now
                        && CategoryRules.Similarity(category, p.Category) > 0)
            .OrderBy(p => p.OccurredAt)
            .ToList();

        var count = priors.Count;
        var isRecurring = count >= o.RecurringMinPriorIncidents;
        var risk = count switch { 0 => RecurrenceRisk.None, 1 => RecurrenceRisk.Low, 2 => RecurrenceRisk.Medium, _ => RecurrenceRisk.High };
        var total = count + 1; // includes the incident being evaluated
        var months = Math.Max(1, (int)Math.Round(o.RecurrenceWindowDays / 30.4));
        var summary = count == 0
            ? "No related incidents recorded at this location."
            : $"{total} related incidents within {(o.RecurrenceWindowDays >= 360 ? "12 months" : $"{months} months")}.";

        return new RecurrenceResult(count, total, o.RecurrenceWindowDays, isRecurring, risk, ScoreFor(count), priors, summary);
    }
}
