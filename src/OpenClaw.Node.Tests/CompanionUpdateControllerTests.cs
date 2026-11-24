using OpenClaw.Node.Services;
using OpenClaw.Node.Tray;
using System.IO;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace OpenClaw.Node.Tests;

public class CompanionUpdateControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrayRefreshesTheSignedFeedBeforeOfferingToRestart(bool feedUnavailable)
    {
        var source = new TestFeed();
        var manager = new RecordingUpdateManager(source);
        var exited = false;
        var controller = new CompanionUpdateController(manager, default, () => exited = true);
        await controller.PrepareAsync();
        Assert.Equal("Restart to update...", controller.MenuText);

        source.Version = "1.0.7";
        source.Unavailable = feedUnavailable;
        string? offeredVersion = null;
        string? message = null;
        await controller.CheckFromTrayAsync(
            asset => { offeredVersion = asset.Version.ToString(); return false; },
            text => message = text);

        Assert.False(exited);
        if (feedUnavailable)
        {
            Assert.Null(offeredVersion);
            Assert.StartsWith("Update not applied:", message);
            Assert.Equal(new[] { "1.0.6" }, manager.Downloaded);
        }
        else
        {
            Assert.Equal("1.0.7", offeredVersion);
            Assert.Null(message);
            Assert.Equal(new[] { "1.0.6", "1.0.7" }, manager.Downloaded);
        }
    }

    private sealed class RecordingUpdateManager(TestFeed source) : UpdateManager(source,
        locator: new TestVelopackLocator(SignedReleaseSource.PackageId, "1.0.5", TestEnvironment.Directory))
    {
        internal List<string> Downloaded { get; } = [];

        public override Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null,
            CancellationToken cancelToken = default)
        {
            Downloaded.Add(updates.TargetFullRelease.Version.ToString());
            return Task.CompletedTask;
        }
    }

    // Stands in for the already verified feed. Signature verification is exercised separately.
    private sealed class TestFeed : IUpdateSource
    {
        internal string Version { get; set; } = "1.0.6";
        internal bool Unavailable { get; set; }

        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            if (Unavailable) throw new IOException("Feed unavailable.");
            return Task.FromResult(VelopackAssetFeed.FromJson($$"""
                {"Assets":[{"PackageId":"{{SignedReleaseSource.PackageId}}","Version":"{{Version}}","Type":"Full",
                "FileName":"{{SignedReleaseSource.PackageId}}-{{Version}}-full.nupkg"}]}
                """));
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken = default) => throw new NotSupportedException();
    }
}
