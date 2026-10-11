using CivicLens.Domain.Configuration;

namespace CivicLens.Domain.Scoring;

public sealed record VoteInput(CitizenVote Vote, int VoterTrustScore);

public sealed record CitizenVerificationSummary(int Fixed, int StillExists, int Unsure, int Total, double? Confidence)
{
    public int Decisive => Fixed + StillExists;
}

public enum ResolutionOutcome { Pending, Verified, Disputed }

public sealed record ResolutionAssessment(double Confidence, ResolutionOutcome Outcome, CitizenVerificationSummary Citizens, string Reason);

/// <summary>
/// Combines the AI before/after check with citizen votes. Physical evidence always wins:
/// a failed AI check can never be "voted away", and trust only weights votes between 0.5x and 1.0x.
/// </summary>
public static class ResolutionConfidenceCalculator
{
    public static CitizenVerificationSummary Summarize(IEnumerable<VoteInput> votes)
    {
        var list = votes.ToList();
        double Weight(VoteInput v) => 0.5 + Math.Clamp(v.VoterTrustScore, 0, 100) / 200.0;
        var fixedW = list.Where(v => v.Vote == CitizenVote.Fixed).Sum(Weight);
        var notW = list.Where(v => v.Vote == CitizenVote.StillExists).Sum(Weight);
        double? conf = fixedW + notW > 0 ? Math.Round(fixedW / (fixedW + notW), 4) : null;
        return new CitizenVerificationSummary(
            list.Count(v => v.Vote == CitizenVote.Fixed), list.Count(v => v.Vote == CitizenVote.StillExists),
            list.Count(v => v.Vote == CitizenVote.Unsure), list.Count, conf);
    }

    public static ResolutionAssessment Assess(double aiConfidence, IEnumerable<VoteInput> votes, ScoringOptions o)
    {
        var ai = Math.Clamp(aiConfidence, 0, 1);
        var c = Summarize(votes);
        var combined = c.Confidence is null ? ai : o.AiWeightInResolution * ai + (1 - o.AiWeightInResolution) * c.Confidence.Value;
        combined = Math.Round(combined, 4);

        if (ai < o.ResolutionDisputedThreshold)
            return new(combined, ResolutionOutcome.Disputed, c, "Visual verification indicates the issue may still be present.");
        if (c.Decisive >= 2 && c.Confidence < 0.4)
            return new(combined, ResolutionOutcome.Disputed, c, $"{c.StillExists} of {c.Decisive} citizens report the issue still exists.");
        if (combined < o.ResolutionDisputedThreshold)
            return new(combined, ResolutionOutcome.Disputed, c, "Combined verification confidence is below the review threshold.");
        if (combined >= o.ResolutionVerifiedThreshold && c.Fixed >= o.MinCitizenVotesForVerification)
            return new(combined, ResolutionOutcome.Verified, c, "Visual verification and citizen confirmation agree the issue is resolved.");

        return new(combined, ResolutionOutcome.Pending, c,
            c.Fixed < o.MinCitizenVotesForVerification
                ? "Waiting for citizen confirmation."
                : "Verification confidence is not yet sufficient.");
    }
}
