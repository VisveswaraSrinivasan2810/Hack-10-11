using CivicLens.Application.Abstractions;
using CivicLens.Application.Demo;
using CivicLens.Domain;
using CivicLens.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CivicLens.Tests.Unit;

public class DemoProviderTests
{
    private readonly DemoClassificationService _cls = new();
    private readonly DemoResolutionVerificationService _ver = new();
    private readonly ImageSimilarityService _sim = new();

    [Fact]
    public async Task Filename_hint_gives_high_confidence_and_is_deterministic()
    {
        var img = new ImageUpload(DemoImages.Bytes("x"), "pothole_school.jpg", "image/jpeg");
        var a = await _cls.ClassifyAsync(img, null);
        var b = await _cls.ClassifyAsync(img, null);
        Assert.Equal(IssueCategory.Pothole, a.Category);
        Assert.InRange(a.Confidence, 0.94, 0.98);
        Assert.Equal((a.Category, a.Confidence, a.Severity), (b.Category, b.Confidence, b.Severity));
    }

    [Fact]
    public async Task No_hint_is_low_confidence_with_three_options()
    {
        var r = await _cls.ClassifyAsync(new ImageUpload(DemoImages.Bytes("x"), "IMG_0001.jpg", "image/jpeg"), null);
        Assert.Equal(0.51, r.Confidence);
        Assert.Equal(3, r.Alternatives.Count);
    }

    [Fact]
    public async Task Description_hint_is_used_with_lower_confidence()
    {
        var r = await _cls.ClassifyAsync(new ImageUpload(DemoImages.Bytes("x"), "IMG_1.jpg", "image/jpeg"), "big water leak on the road");
        Assert.Equal(IssueCategory.WaterLeakage, r.Category);
        Assert.InRange(r.Confidence, 0.86, 0.90);
    }

    [Fact]
    public async Task After_photo_verification_distinguishes_fixed_from_still_present()
    {
        var ok = await _ver.VerifyAsync(IssueCategory.Pothole, new ImageUpload(DemoImages.Bytes("a"), "after_fixed.jpg", "image/jpeg"), null);
        var bad = await _ver.VerifyAsync(IssueCategory.Pothole, new ImageUpload(DemoImages.Bytes("a"), "pothole_still_present.jpg", "image/jpeg"), null);
        Assert.Equal(VerificationResult.IssueNotDetected, ok.Result);
        Assert.InRange(ok.Confidence, 0.9, 1);
        Assert.Equal(VerificationResult.IssueStillPresent, bad.Result);
        Assert.InRange(bad.Confidence, 0, 0.3);
    }

    [Fact]
    public async Task Similarity_identical_hash_is_one_and_signature_is_stable()
    {
        var bytes = DemoImages.Bytes("a");
        var sig = _sim.ComputeSignature(bytes);
        Assert.Equal(sig, _sim.ComputeSignature(bytes));
        Assert.Equal(1.0, await _sim.CompareAsync(sig, "h", sig, "h"));
        Assert.InRange(await _sim.CompareAsync(sig, "h1", _sim.ComputeSignature(DemoImages.Bytes("b")), "h2"), 0.8, 1.0);
    }
}

public class ClaudeFallbackTests
{
    private static readonly Microsoft.Extensions.Configuration.IConfiguration Demo = Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(new Microsoft.Extensions.Configuration.ConfigurationBuilder(), new Dictionary<string, string?> { ["DEMO_MODE"] = "true" }).Build();
    [Fact]
    public async Task Real_provider_failure_falls_back_to_demo_provider()
    {
        // No API key and an unreachable base URL: the real call must fail, and the decorator must still answer.
        var opts = Options.Create(new AiOptions { Provider = "Claude", ApiKey = "sk-ant-invalid-test-key" });
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "http://127.0.0.1:9");
        try
        {
            var claude = new ClaudeVisionClient(opts);
            var svc = new FallbackClassificationService(new ClaudeClassificationService(claude), new DemoClassificationService(), Demo, NullLogger<FallbackClassificationService>.Instance);
            var r = await svc.ClassifyAsync(new ImageUpload(DemoImages.Bytes("x"), "pothole.jpg", "image/jpeg"), null);
            Assert.Equal("demo", r.Provider);
            Assert.Equal(IssueCategory.Pothole, r.Category);

            var ver = new FallbackVerificationService(new ClaudeVerificationService(claude), new DemoResolutionVerificationService(), Demo, NullLogger<FallbackVerificationService>.Instance);
            var v = await ver.VerifyAsync(IssueCategory.Pothole, new ImageUpload(DemoImages.Bytes("x"), "after_fixed.jpg", "image/jpeg"), null);
            Assert.Equal("demo", v.Provider);
        }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", null); }
    }
}
