using OpenClaw.Node.Services;
using System.IO;
using Velopack;
using Xunit;

namespace OpenClaw.Node.Tests;

public class SignedReleaseTests
{
    [Fact]
    public void AValidRepositorySubjectCannotSubstituteForTheWorkflowCertificateUri()
    {
        const string slug = "example/companion";
        const string expected = "https://github.com/example/companion/.github/workflows/windows-release.yml@refs/heads/main";
        SignedReleaseSource.RequireReleaseWorkflow(slug, expected);
        foreach (var other in new[] { null, "repo:example/companion:ref:refs/heads/main",
                 expected.Replace("windows-release", "other"), expected.Replace("heads/main", "heads/other"),
                 expected.Replace("example/companion", "example/fork") })
            Assert.Throws<InvalidDataException>(() => SignedReleaseSource.RequireReleaseWorkflow(slug, other));
    }

    [Theory]
    [InlineData("")]
    [InlineData("team/..")]
    [InlineData("team/project/other")]
    [InlineData("https://example.org/team/project")]
    public void RepositoryMustBeABuildTimeGitHubSlug(string slug)
        => Assert.Throws<InvalidDataException>(() => SignedReleaseSource.RepositoryUrl(slug));

    private static VelopackAsset Asset() => VelopackAssetFeed.FromJson("""
      {"Assets":[{"PackageId":"OpenClawCompanion","Version":"1.0.42","Type":"Full",
      "FileName":"OpenClawCompanion-1.0.42-full.nupkg","Size":100,
      "SHA256":"0000000000000000000000000000000000000000000000000000000000000000"}]}
      """).Assets.Single();

    [Fact]
    public void WindowsFeedCannotSelectAnExternalPackageOrDowngradeToAnUnsignedDigest()
    {
        var a = Asset();
        SignedReleaseSource.Validate(a);
        a.FileName = "../other.nupkg";
        Assert.Throws<InvalidDataException>(() => SignedReleaseSource.Validate(a));
        a = Asset(); a.SHA256 = "";
        Assert.Throws<InvalidDataException>(() => SignedReleaseSource.Validate(a));
        a = Asset(); a.Type = VelopackAssetType.Delta;
        Assert.Throws<InvalidDataException>(() => SignedReleaseSource.Validate(a));
    }

    // The publishing job runs the production verifier against the actual keyless signature,
    // and requires the same signature to be rejected after one byte of the feed changes.
    [Fact]
    public async Task VerifyCiSignatureBeforePublishing()
    {
        var dir = Environment.GetEnvironmentVariable("OPENCLAW_RELEASE_VERIFY_DIR");
        if (dir == null) return;
        var path = Path.Combine(dir, "releases.win.json");
        var bytes = await File.ReadAllBytesAsync(path);
        var bundle = await File.ReadAllTextAsync(path + ".sigstore.json");
        var asset = (await SignedReleaseSource.VerifyFeedAsync(bytes, bundle, default)).Assets.Single();
        var file = new FileInfo(Path.Combine(dir, asset.FileName));
        Assert.Equal(asset.Size, file.Length);
        using var stream = file.OpenRead();
        Assert.Equal(asset.SHA256.ToUpperInvariant(), Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)));
        await Assert.ThrowsAnyAsync<Exception>(() => SignedReleaseSource.VerifyFeedAsync([..bytes, (byte)' '], bundle, default));
    }
}
