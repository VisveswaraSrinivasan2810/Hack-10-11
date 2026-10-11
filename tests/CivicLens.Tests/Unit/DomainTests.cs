using CivicLens.Application.Common;
using CivicLens.Domain;
using CivicLens.Domain.Configuration;
using CivicLens.Domain.Scoring;

namespace CivicLens.Tests.Unit;

public class GeoMathTests
{
    [Fact]
    public void Distance_between_identical_points_is_zero() =>
        Assert.Equal(0, GeoMath.DistanceMeters(12.97, 77.59, 12.97, 77.59), 3);

    [Fact]
    public void Distance_of_one_degree_latitude_is_about_111km()
    {
        var d = GeoMath.DistanceMeters(0, 0, 1, 0);
        Assert.InRange(d, 111_000, 111_400);
    }

    [Fact]
    public void Distance_is_symmetric() =>
        Assert.Equal(GeoMath.DistanceMeters(12.97, 77.59, 12.98, 77.60), GeoMath.DistanceMeters(12.98, 77.60, 12.97, 77.59), 6);

    [Fact]
    public void Demo_school_pothole_is_about_72_metres_from_school() =>
        Assert.InRange(GeoMath.DistanceMeters(12.97185, 77.5940, 12.9712, 77.5940), 70, 74);

    [Fact]
    public void Bounding_box_contains_points_inside_radius()
    {
        var (minLat, maxLat, minLon, maxLon) = GeoMath.BoundingBox(12.97, 77.59, 50);
        Assert.InRange(12.97 + 0.0004, minLat, maxLat);
        Assert.InRange(77.59 + 0.0004, minLon, maxLon);
    }

    [Theory]
    [InlineData(91, 0, false)] [InlineData(0, 181, false)] [InlineData(double.NaN, 0, false)] [InlineData(12.9, 77.5, true)]
    public void Coordinate_validation(double lat, double lon, bool expected) => Assert.Equal(expected, GeoMath.IsValidCoordinate(lat, lon));
}

public class SameIncidentScorerTests
{
    private readonly ScoringOptions _o = new();

    [Fact]
    public void Same_category_11m_with_similar_photo_is_very_likely_same()
    {
        var s = SameIncidentScorer.Score(11, IssueCategory.Pothole, IssueCategory.Pothole, 0.95, 0.1, _o);
        Assert.Equal(MatchVerdict.VeryLikelySame, s.Verdict);
        Assert.InRange(s.Total, 0.93, 0.99);
    }

    [Fact]
    public void Weights_follow_the_specification()
    {
        // all components = 1 → total 1.0 ; only category = 1 → 0.30
        Assert.Equal(1.0, SameIncidentScorer.Score(0, IssueCategory.Pothole, IssueCategory.Pothole, 1, 0, _o).Total, 4);
        Assert.Equal(0.30, SameIncidentScorer.Score(999, IssueCategory.Pothole, IssueCategory.Pothole, 0, 999, _o).Total, 4);
    }

    [Fact]
    public void Mid_distance_is_a_possible_match_the_citizen_must_decide()
    {
        var s = SameIncidentScorer.Score(30, IssueCategory.Pothole, IssueCategory.Pothole, 0.8, 1, _o);
        Assert.Equal(MatchVerdict.PossibleMatch, s.Verdict);
    }

    [Fact]
    public void Edge_of_radius_is_a_new_incident()
    {
        var s = SameIncidentScorer.Score(48, IssueCategory.Pothole, IssueCategory.Pothole, 0.5, 5, _o);
        Assert.Equal(MatchVerdict.LikelyNew, s.Verdict);
    }

    [Fact]
    public void Different_category_at_same_spot_is_not_merged()
    {
        var s = SameIncidentScorer.Score(2, IssueCategory.GarbageAccumulation, IssueCategory.Pothole, 0.9, 0, _o);
        Assert.NotEqual(MatchVerdict.VeryLikelySame, s.Verdict);
    }

    [Fact]
    public void Related_categories_score_between_same_and_unrelated()
    {
        Assert.Equal(0.7, CategoryRules.Similarity(IssueCategory.Pothole, IssueCategory.RoadDamage));
        Assert.Equal(0, CategoryRules.Similarity(IssueCategory.Pothole, IssueCategory.WaterLeakage));
    }
}

public class CpsTests
{
    private readonly ScoringOptions _o = new();

    [Fact]
    public void Default_weights_sum_to_one() =>
        Assert.Equal(1.0, _o.WeightSeverity + _o.WeightCitizenEvidence + _o.WeightLocationRisk + _o.WeightIncidentAge + _o.WeightExposure + _o.WeightRecurrence, 6);

    [Theory]
    [InlineData(0, PriorityBand.Low)] [InlineData(29.9, PriorityBand.Low)] [InlineData(30, PriorityBand.Moderate)]
    [InlineData(49.9, PriorityBand.Moderate)] [InlineData(50, PriorityBand.High)] [InlineData(70, PriorityBand.VeryHigh)]
    [InlineData(84.9, PriorityBand.VeryHigh)] [InlineData(85, PriorityBand.Critical)] [InlineData(100, PriorityBand.Critical)]
    public void Band_boundaries(double score, PriorityBand expected) => Assert.Equal(expected, CpsCalculator.BandFor(score));

    [Fact]
    public void Score_is_the_weighted_sum_of_the_factors_when_amplifier_is_disabled()
    {
        var plain = new ScoringOptions { ContextAmplifierMax = 0 };
        var r = CpsCalculator.Calculate(new CpsInput(IssueCategory.Pothole, 80, 3, 90, 7, 70, 2), plain);
        var expected = r.Severity * .25 + r.CitizenEvidence * .15 + r.LocationRisk * .20 + r.IncidentAge * .10 + r.Exposure * .15 + r.Recurrence * .15;
        Assert.Equal(Math.Round(expected, 1), r.FinalScore, 1);
    }

    [Fact]
    public void More_observations_raise_the_score_with_diminishing_returns()
    {
        double Score(double n) => CpsCalculator.Calculate(new CpsInput(IssueCategory.Pothole, 70, n, 50, 0, 40, 0), _o).FinalScore;
        Assert.True(Score(2) > Score(1));
        Assert.True(Score(7) > Score(2));
        Assert.True(Score(2) - Score(1) > Score(8) - Score(7));
    }

    [Fact]
    public void Recurrence_increases_the_score()
    {
        double Score(int prior) => CpsCalculator.Calculate(new CpsInput(IssueCategory.RoadDamage, 70, 1, 30, 0, 40, prior), _o).FinalScore;
        Assert.True(Score(3) > Score(0));
    }

    [Fact]
    public void Score_is_clamped_and_explained()
    {
        var r = CpsCalculator.Calculate(new CpsInput(IssueCategory.FallenTree, 500, 99, 500, 999, 500, 9, ["Near a school"]), _o);
        Assert.InRange(r.FinalScore, 99, 100);
        Assert.Equal(PriorityBand.Critical, r.Band);
        Assert.Contains(r.Reasons, x => x.Contains("Near a school"));
        Assert.Contains(r.Reasons, x => x.Contains("Recurring"));
    }

    [Fact]
    public void Weights_are_configurable()
    {
        var onlySeverity = new ScoringOptions { ContextAmplifierMax = 0, WeightSeverity = 1, WeightCitizenEvidence = 0, WeightLocationRisk = 0, WeightIncidentAge = 0, WeightExposure = 0, WeightRecurrence = 0 };
        Assert.Equal(60, CpsCalculator.Calculate(new CpsInput(IssueCategory.Pothole, 60, 5, 100, 100, 100, 5), onlySeverity).FinalScore);
    }
}

public class ContextRiskTests
{
    [Fact]
    public void Quiet_road_has_baseline_risk()
    {
        var r = ContextRiskCalculator.Calculate(IssueCategory.Pothole, []);
        Assert.Equal(ContextRiskCalculator.BaselineRisk, r.LocationRisk);
        Assert.Empty(r.Reasons);
    }

    [Fact]
    public void School_72m_away_is_high_risk_and_explained()
    {
        var r = ContextRiskCalculator.Calculate(IssueCategory.Pothole, [new PoiHit("Demo Public School", PoiType.School, 100, 72)]);
        Assert.True(r.LocationRisk >= 75, $"risk was {r.LocationRisk}");
        Assert.Equal("HIGH", ContextRiskCalculator.RiskLabel(r.LocationRisk));
        Assert.Contains("72 m from a school", r.Reasons[0]);
    }

    [Fact]
    public void Poi_outside_its_influence_radius_is_ignored()
    {
        var r = ContextRiskCalculator.Calculate(IssueCategory.Pothole, [new PoiHit("Far School", PoiType.School, 100, 500)]);
        Assert.Equal(ContextRiskCalculator.BaselineRisk, r.LocationRisk);
    }

    [Fact]
    public void Closer_poi_means_higher_risk()
    {
        double Risk(double d) => ContextRiskCalculator.Calculate(IssueCategory.Pothole, [new PoiHit("S", PoiType.School, 100, d)]).LocationRisk;
        Assert.True(Risk(20) > Risk(150));
    }
}

public class RecurrenceTests
{
    private readonly ScoringOptions _o = new();
    private static readonly DateTime Oct = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Demo_story_pothole_road_damage_pothole_then_road_damage_is_four_related_incidents()
    {
        var history = new[]
        {
            new PriorIncident(Guid.NewGuid(), "CL-431", IssueCategory.Pothole, new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc), 4),
            new PriorIncident(Guid.NewGuid(), "CL-692", IssueCategory.RoadDamage, new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc), 6),
            new PriorIncident(Guid.NewGuid(), "CL-811", IssueCategory.Pothole, new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc), 3),
        };
        var r = RecurrenceEvaluator.Evaluate(IssueCategory.RoadDamage, Oct, history, _o);
        Assert.True(r.IsRecurring);
        Assert.Equal(3, r.PriorCount);
        Assert.Equal(4, r.RelatedTotal);
        Assert.Equal(RecurrenceRisk.High, r.Risk);
        Assert.Contains("4 related incidents within 12 months", r.Summary);
    }

    [Fact]
    public void Old_or_distant_or_unrelated_history_is_ignored()
    {
        var history = new[]
        {
            new PriorIncident(Guid.NewGuid(), "A", IssueCategory.Pothole, Oct.AddDays(-400), 4),             // too old
            new PriorIncident(Guid.NewGuid(), "B", IssueCategory.Pothole, Oct.AddDays(-30), 300),            // too far
            new PriorIncident(Guid.NewGuid(), "C", IssueCategory.WaterLeakage, Oct.AddDays(-30), 4),         // unrelated
        };
        var r = RecurrenceEvaluator.Evaluate(IssueCategory.Pothole, Oct, history, _o);
        Assert.Equal(0, r.PriorCount);
        Assert.False(r.IsRecurring);
        Assert.Equal(0, r.Score);
    }

    [Fact]
    public void Recurrence_score_grows_with_history() =>
        Assert.True(RecurrenceEvaluator.ScoreFor(3) > RecurrenceEvaluator.ScoreFor(1));
}

public class StateMachineTests
{
    [Theory]
    [InlineData(IncidentStatus.Reported, IncidentStatus.Assigned)]
    [InlineData(IncidentStatus.Assigned, IncidentStatus.Accepted)]
    [InlineData(IncidentStatus.Accepted, IncidentStatus.InProgress)]
    [InlineData(IncidentStatus.InProgress, IncidentStatus.WorkCompleted)]
    [InlineData(IncidentStatus.WorkCompleted, IncidentStatus.AwaitingVerification)]
    [InlineData(IncidentStatus.AwaitingVerification, IncidentStatus.VerifiedResolved)]
    [InlineData(IncidentStatus.AwaitingVerification, IncidentStatus.VerificationFailed)]
    [InlineData(IncidentStatus.VerificationFailed, IncidentStatus.RequiresReview)]
    [InlineData(IncidentStatus.VerifiedResolved, IncidentStatus.Reopened)]
    public void Allowed_transitions(IncidentStatus from, IncidentStatus to) => Assert.True(IncidentStateMachine.IsAllowed(from, to));

    [Theory]
    [InlineData(IncidentStatus.Reported, IncidentStatus.VerifiedResolved)]       // cannot skip verification
    [InlineData(IncidentStatus.InProgress, IncidentStatus.VerifiedResolved)]
    [InlineData(IncidentStatus.WorkCompleted, IncidentStatus.VerifiedResolved)]  // work completed ≠ resolved
    [InlineData(IncidentStatus.VerificationFailed, IncidentStatus.VerifiedResolved)]
    [InlineData(IncidentStatus.Rejected, IncidentStatus.Assigned)]
    [InlineData(IncidentStatus.Assigned, IncidentStatus.WorkCompleted)]
    public void Forbidden_transitions(IncidentStatus from, IncidentStatus to) => Assert.False(IncidentStateMachine.IsAllowed(from, to));

    [Fact]
    public void Only_the_system_can_declare_verification_failure_and_workers_cannot_resolve()
    {
        Assert.False(IncidentStateMachine.IsAllowed(IncidentStatus.AwaitingVerification, IncidentStatus.VerificationFailed, TransitionActor.Authority));
        Assert.False(IncidentStateMachine.IsAllowed(IncidentStatus.AwaitingVerification, IncidentStatus.VerifiedResolved, TransitionActor.Worker));
        Assert.True(IncidentStateMachine.IsAllowed(IncidentStatus.Assigned, IncidentStatus.Accepted, TransitionActor.Worker));
        Assert.False(IncidentStateMachine.IsAllowed(IncidentStatus.Assigned, IncidentStatus.Accepted, TransitionActor.Citizen));
    }

    [Fact]
    public void Resolution_status_is_independent_of_work_completion()
    {
        Assert.Equal(ResolutionStatus.AwaitingVerification, IncidentStateMachine.ResolutionFor(IncidentStatus.WorkCompleted));
        Assert.Equal(ResolutionStatus.Disputed, IncidentStateMachine.ResolutionFor(IncidentStatus.RequiresReview));
        Assert.Equal(ResolutionStatus.VerifiedResolved, IncidentStateMachine.ResolutionFor(IncidentStatus.VerifiedResolved));
    }
}

public class ResolutionConfidenceTests
{
    private readonly ScoringOptions _o = new();
    private static VoteInput V(CitizenVote v, int trust = 50) => new(v, trust);

    [Fact]
    public void High_ai_confidence_and_citizen_confirmation_verifies()
    {
        var a = ResolutionConfidenceCalculator.Assess(0.94, [V(CitizenVote.Fixed), V(CitizenVote.Fixed), V(CitizenVote.Fixed), V(CitizenVote.Fixed), V(CitizenVote.StillExists)], _o);
        Assert.Equal(ResolutionOutcome.Verified, a.Outcome);
        Assert.InRange(a.Confidence, 0.85, 0.95);
    }

    [Fact]
    public void High_ai_confidence_alone_waits_for_citizens()
    {
        var a = ResolutionConfidenceCalculator.Assess(0.95, [], _o);
        Assert.Equal(ResolutionOutcome.Pending, a.Outcome);
    }

    [Fact]
    public void Ghost_closure_low_ai_confidence_is_disputed_even_if_a_citizen_says_fixed()
    {
        var a = ResolutionConfidenceCalculator.Assess(0.24, [V(CitizenVote.Fixed, 100)], _o);
        Assert.Equal(ResolutionOutcome.Disputed, a.Outcome);
    }

    [Fact]
    public void Four_of_five_citizens_saying_unresolved_disputes_despite_good_ai_score()
    {
        var votes = new[] { V(CitizenVote.StillExists), V(CitizenVote.StillExists), V(CitizenVote.StillExists), V(CitizenVote.StillExists), V(CitizenVote.Fixed) };
        var a = ResolutionConfidenceCalculator.Assess(0.9, votes, _o);
        Assert.Equal(ResolutionOutcome.Disputed, a.Outcome);
        Assert.Contains("4 of 5", a.Reason);
    }

    [Fact]
    public void Unsure_votes_do_not_count_either_way()
    {
        var s = ResolutionConfidenceCalculator.Summarize([V(CitizenVote.Unsure), V(CitizenVote.Unsure)]);
        Assert.Null(s.Confidence);
        Assert.Equal(2, s.Unsure);
    }

    [Fact]
    public void Trust_weights_votes_but_never_dominates()
    {
        var s = ResolutionConfidenceCalculator.Summarize([V(CitizenVote.Fixed, 0), V(CitizenVote.StillExists, 100)]);
        Assert.InRange(s.Confidence!.Value, 0.25, 0.45); // weight 0.5 vs 1.0
    }
}

public class HealthScoreTests
{
    [Fact]
    public void Clean_zone_is_perfect() => Assert.Equal(100, HealthScoreCalculator.Calculate(new ZoneStats(0, 0, 0, 0, 0)).Score);

    [Fact]
    public void Penalties_are_applied_and_explained()
    {
        var r = HealthScoreCalculator.Calculate(new ZoneStats(5, 1, 1, 1, 24));
        Assert.Equal(100 - 15 - 8 - 5 - 8, r.Score);
        Assert.Equal(HealthBand.NeedsAttention, r.Band);
        Assert.Equal(4, r.Penalties.Count);
    }

    [Fact]
    public void Penalties_are_capped_and_score_never_negative()
    {
        var r = HealthScoreCalculator.Calculate(new ZoneStats(500, 500, 500, 500, 9999));
        Assert.InRange(r.Score, 0, 100);
        Assert.Equal(HealthBand.Critical, r.Band);
    }

    [Theory]
    [InlineData(91, HealthBand.Healthy)] [InlineData(67, HealthBand.NeedsAttention)] [InlineData(41, HealthBand.Critical)]
    public void Spec_example_bands(double score, HealthBand band) => Assert.Equal(band, HealthScoreCalculator.BandFor(score));
}

public class TrustScoreTests
{
    [Fact]
    public void New_user_starts_at_base() => Assert.Equal(TrustScoreCalculator.BaseScore, TrustScoreCalculator.Calculate(new TrustInput(0, 0, 0, 0, 0)).Score);

    [Fact]
    public void Spec_example_scores_high_but_below_100() =>
        Assert.InRange(TrustScoreCalculator.Calculate(new TrustInput(18, 18, 11, 7, 1)).Score, 80, 95);

    [Fact]
    public void Volume_alone_cannot_game_the_score()
    {
        // 500 observations that were never verified: no gain. 500 verified: capped.
        Assert.Equal(TrustScoreCalculator.BaseScore, TrustScoreCalculator.Calculate(new TrustInput(0, 0, 0, 0, 0)).Score);
        Assert.True(TrustScoreCalculator.Calculate(new TrustInput(500, 500, 500, 500, 0)).Score <= 100);
        Assert.Equal(
            TrustScoreCalculator.Calculate(new TrustInput(40, 0, 0, 0, 0)).Score,
            TrustScoreCalculator.Calculate(new TrustInput(500, 0, 0, 0, 0)).Score);
    }

    [Fact]
    public void Invalid_submissions_reduce_trust() =>
        Assert.True(TrustScoreCalculator.Calculate(new TrustInput(10, 10, 0, 0, 3)).Score < TrustScoreCalculator.Calculate(new TrustInput(10, 10, 0, 0, 0)).Score);

    [Fact]
    public void Evidence_weight_is_bounded() { Assert.Equal(0.8, TrustScoreCalculator.EvidenceWeight(0), 3); Assert.Equal(1.2, TrustScoreCalculator.EvidenceWeight(100), 3); }
}

public class CorrelationRuleTests
{
    private readonly ScoringOptions _o = new();
    private static readonly DateTime Leak = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Water_leakage_then_road_damage_within_50m_and_30_days_is_a_possible_correlation()
    {
        var m = CorrelationRules.TryCorrelate(IssueCategory.WaterLeakage, Leak, IssueCategory.RoadDamage, Leak.AddDays(13), 18, _o);
        Assert.NotNull(m);
        Assert.Equal("Moderate", m!.ConfidenceLabel);
        Assert.Contains("may have contributed", m.Explanation);
        Assert.DoesNotContain("caused", m.Explanation);
    }

    [Fact]
    public void Wrong_order_distance_window_or_category_do_not_correlate()
    {
        Assert.Null(CorrelationRules.TryCorrelate(IssueCategory.WaterLeakage, Leak, IssueCategory.RoadDamage, Leak.AddDays(-3), 18, _o));
        Assert.Null(CorrelationRules.TryCorrelate(IssueCategory.WaterLeakage, Leak, IssueCategory.RoadDamage, Leak.AddDays(13), 80, _o));
        Assert.Null(CorrelationRules.TryCorrelate(IssueCategory.WaterLeakage, Leak, IssueCategory.RoadDamage, Leak.AddDays(45), 18, _o));
        Assert.Null(CorrelationRules.TryCorrelate(IssueCategory.GarbageAccumulation, Leak, IssueCategory.RoadDamage, Leak.AddDays(13), 18, _o));
    }
}

public class HotspotTests
{
    private readonly ScoringOptions _o = new();
    private static readonly DateTime Now = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    private static HotspotPoint P(double lat, double lon, int daysAgo, double cps = 60, IssueCategory c = IssueCategory.Pothole) =>
        new(Guid.NewGuid(), lat, lon, c, cps, false, Now.AddDays(-daysAgo));

    [Fact]
    public void Nearby_incidents_form_a_hotspot_with_trend_and_isolated_ones_do_not()
    {
        var points = new[]
        {
            P(12.9730, 77.5985, 2), P(12.9731, 77.5986, 4), P(12.9729, 77.5984, 6, c: IssueCategory.WaterLeakage), P(12.9732, 77.5987, 20),
            P(12.9900, 77.6200, 3), // isolated
        };
        var hs = HotspotDetector.Detect(points, Now, _o);
        Assert.Single(hs);
        Assert.Equal(4, hs[0].IncidentCount);
        Assert.Equal(3, hs[0].Last14Days);
        Assert.Equal(1, hs[0].Previous14Days);
        Assert.Equal(200, hs[0].TrendPercent);
        Assert.True(hs[0].IsEmerging);
    }

    [Fact]
    public void Fewer_than_minimum_is_not_a_hotspot() =>
        Assert.Empty(HotspotDetector.Detect([P(12.97, 77.59, 1), P(12.9701, 77.5901, 2)], Now, _o));
}

public class ImageToolsTests
{
    [Fact]
    public void Jpeg_exif_segment_is_stripped_and_image_data_kept()
    {
        byte[] exif = [0xFF, 0xE1, 0x00, 0x08, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0];
        byte[] jfif = [0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00];
        byte[] rest = [0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0xFF, 0xD9];
        var jpg = new byte[] { 0xFF, 0xD8 }.Concat(jfif).Concat(exif).Concat(rest).ToArray();

        var stripped = ImageTools.StripMetadata(jpg);

        Assert.True(stripped.Length < jpg.Length);
        Assert.DoesNotContain((byte)'E', stripped.Skip(2).Take(10)); // EXIF header gone
        Assert.Equal(rest, stripped.Skip(stripped.Length - rest.Length).ToArray());
    }

    [Fact]
    public void Validation_uses_magic_bytes_not_extension()
    {
        Assert.Equal(".jpg", ImageTools.ValidateAndGetExtension([0xFF, 0xD8, 0xFF, 0xE0, 1, 2]));
        Assert.Equal(".png", ImageTools.ValidateAndGetExtension([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Throws<BadRequestException>(() => ImageTools.ValidateAndGetExtension(System.Text.Encoding.ASCII.GetBytes("<html>not an image</html>")));
        Assert.Throws<BadRequestException>(() => ImageTools.ValidateAndGetExtension([]));
        Assert.Throws<BadRequestException>(() => ImageTools.ValidateAndGetExtension(new byte[ImageTools.MaxBytes + 1]));
    }

    [Fact]
    public void Same_bytes_have_the_same_hash() =>
        Assert.Equal(ImageTools.Sha256Hex([1, 2, 3]), ImageTools.Sha256Hex([1, 2, 3]));
}

public class CpsContextAmplifierTests
{
    [Fact]
    public void Plain_weighted_formula_cannot_exceed_75_for_a_brand_new_incident_which_is_why_the_amplifier_exists()
    {
        var plain = new ScoringOptions { ContextAmplifierMax = 0 };
        var best = CpsCalculator.Calculate(new CpsInput(IssueCategory.Pothole, 100, 99, 100, 0, 100, 0), plain);
        Assert.InRange(best.FinalScore, 74, 75.1);
    }

    [Fact]
    public void Quiet_road_is_not_amplified_but_school_adjacent_hazard_is()
    {
        var o = new ScoringOptions();
        var quiet = CpsCalculator.Calculate(new CpsInput(IssueCategory.Pothole, 75, 2, 30, 0, 40, 0), o);
        var school = CpsCalculator.Calculate(new CpsInput(IssueCategory.Pothole, 75, 2, 90, 0, 85, 0), o);
        Assert.Equal(0, quiet.ContextAmplifier);
        Assert.True(school.ContextAmplifier > 0.5);
        Assert.True(school.FinalScore >= 85, $"school pothole scored {school.FinalScore}");
        Assert.Contains(school.Reasons, r => r.Contains("Context amplifier"));
    }

    [Fact]
    public void Jury_story_two_observations_move_a_school_pothole_from_very_high_to_critical()
    {
        var o = new ScoringOptions();
        var risk = ContextRiskCalculator.Calculate(IssueCategory.Pothole, [new PoiHit("Demo Public School", PoiType.School, 100, 72)]);
        double Cps(double obs) => CpsCalculator.Calculate(new CpsInput(IssueCategory.Pothole, 72, obs, risk.LocationRisk, 0, risk.Exposure, 0), o).FinalScore;
        var one = Cps(1.0);
        var two = Cps(2.0);
        Assert.InRange(one, 70, 84.9);
        Assert.True(two >= 85, $"{one} → {two}");
    }
}
