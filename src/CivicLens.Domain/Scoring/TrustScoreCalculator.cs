namespace CivicLens.Domain.Scoring;

public sealed record TrustInput(
    int VerifiedObservations, int AccurateClassifications, int UsefulConfirmations, int ResolutionChecks, int InvalidSubmissions);

public sealed record TrustResult(int Score, TrustInput Inputs, IReadOnlyList<string> Factors);

/// <summary>
/// Citizen trust rewards USEFUL participation, never raw report volume:
/// each positive factor is capped so spamming cannot inflate the score; invalid submissions are penalised.
/// </summary>
public static class TrustScoreCalculator
{
    public const int BaseScore = 45;

    public static TrustResult Calculate(TrustInput t)
    {
        var verified = Math.Min(30, t.VerifiedObservations * 1.5);
        var accurate = Math.Min(5, t.AccurateClassifications * 0.5);
        var confirmations = Math.Min(15, t.UsefulConfirmations * 1.5);
        var checks = Math.Min(10, t.ResolutionChecks * 2.0);
        var invalid = t.InvalidSubmissions * 8.0;

        var score = (int)Math.Round(Math.Clamp(BaseScore + verified + accurate + confirmations + checks - invalid, 0, 100));
        var factors = new List<string>
        {
            $"Verified observations: +{verified:0.#} (cap 30)",
            $"Accurate classifications: +{accurate:0.#} (cap 5)",
            $"Useful confirmations: +{confirmations:0.#} (cap 15)",
            $"Resolution checks: +{checks:0.#} (cap 10)",
            $"Invalid submissions: -{invalid:0.#}"
        };
        return new TrustResult(score, t, factors);
    }

    /// <summary>Trust nudges evidence weight between 0.8x and 1.2x; it can never override physical evidence.</summary>
    public static double EvidenceWeight(int trustScore) => 0.8 + 0.4 * Math.Clamp(trustScore, 0, 100) / 100.0;
}
