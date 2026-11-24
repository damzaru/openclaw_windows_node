using System.Reflection;
using System.Text.Json;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using OpenClaw.Node.Tray;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class BrowserProfileServiceTests
{
    private static readonly string[] PersistentFiles = { "Local State", "Default/Login Data", "Default/Network/Cookies", "Default/Preferences", "Default/Secure Preferences", "Default/History", "Default/Extensions/example/manifest.json", "Default/Local Extension Settings/example/state", "Default/Local Storage/leveldb/data", "Default/IndexedDB/site/data", "Default/Service Worker/CacheStorage/data" };

    [Fact]
    public void ExplicitBackupProfile_MigratesWithoutRequiringTheOldBuildLocation()
    {
        using var fixture = new Fixture();
        var backup = Path.Combine(fixture.Root, "backup/profile");
        fixture.Put(backup, "Local State", "encryption-key");
        fixture.Put(backup, "Default/Extensions/example/manifest.json", "extension");
        var service = new BrowserProfileService(dataDirectory: Path.Combine(fixture.Root, "data"), isInUse: _ => false, legacyDirectory: backup);
        Assert.True(service.Maintain(128, 64).Migrated);
        Assert.False(Directory.Exists(backup));
        Assert.Equal("encryption-key", File.ReadAllText(Path.Combine(service.ProfilePath, "Local State")));
        Assert.Equal("extension", File.ReadAllText(Path.Combine(service.ProfilePath, "Default/Extensions/example/manifest.json")));
    }

    [Fact]
    public void Migration_RejectsUnrecognizedDirectoriesWithoutMovingThem()
    {
        using var fixture = new Fixture();
        fixture.Put(fixture.Service.LegacyPath, "unrelated-file", "keep");
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Maintain(128, 64));
        Assert.True(File.Exists(Path.Combine(fixture.Service.LegacyPath, "unrelated-file")));
        Assert.False(Directory.Exists(fixture.Service.ProfilePath));
    }

    [Fact]
    public void Migration_MovesTheWholeProfileAndIsIdempotent()
    {
        using var fixture = new Fixture();
        foreach (var relative in PersistentFiles) fixture.Put(fixture.Service.LegacyPath, relative, "preserve:" + relative);
        fixture.Put(fixture.Service.LegacyPath, "Default/Cache/data", "cache");
        var result = fixture.Service.Maintain(128, 64);
        Assert.True(result.Migrated);
        Assert.False(Directory.Exists(fixture.Service.LegacyPath));
        foreach (var relative in PersistentFiles) Assert.Equal("preserve:" + relative, File.ReadAllText(Path.Combine(result.ProfilePath, relative)));
        Assert.False(fixture.Service.Maintain(128, 64).Migrated);
    }

    [Fact]
    public void Migration_NeverMergesOrOverwritesAnExistingProfile()
    {
        using var fixture = new Fixture();
        fixture.Put(fixture.Service.LegacyPath, "Local State", "old-key");
        fixture.Put(fixture.Service.ProfilePath, "Local State", "new-key");
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Maintain(128, 64));
        Assert.Equal("old-key", File.ReadAllText(Path.Combine(fixture.Service.LegacyPath, "Local State")));
        Assert.Equal("new-key", File.ReadAllText(Path.Combine(fixture.Service.ProfilePath, "Local State")));
    }

    [Fact]
    public void Migration_RefusesAnActiveLegacyProfile()
    {
        using var fixture = new Fixture(_ => true);
        fixture.Put(fixture.Service.LegacyPath, "Local State", "key");
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Maintain(128, 64));
        Assert.True(File.Exists(Path.Combine(fixture.Service.LegacyPath, "Local State")));
        Assert.False(Directory.Exists(fixture.Service.ProfilePath));
    }

    [Fact]
    public void ActiveProfile_DefersMaintenanceAndRefusesClear()
    {
        using var fixture = new Fixture(_ => true);
        fixture.Put(fixture.Service.ProfilePath, "Default/Cache/data", "in-use");
        Assert.True(fixture.Service.Maintain(128, 64).BrowserRunning);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Maintain(128, 64, clear: true));
        Assert.Equal("in-use", File.ReadAllText(Path.Combine(fixture.Service.ProfilePath, "Default/Cache/data")));
    }

    [Fact]
    public void ClearCache_PreservesLoginKeysExtensionsCookiesAndSiteStorage()
    {
        using var fixture = new Fixture();
        foreach (var relative in PersistentFiles) fixture.Put(fixture.Service.ProfilePath, relative, "preserve:" + relative);
        foreach (var relative in new[] { "Default/Cache/data", "Default/Code Cache/js/data", "Default/GPUCache/data", "Profile 1/Cache/data", "GrShaderCache/data" })
            fixture.Put(fixture.Service.ProfilePath, relative, "cache");
        var result = fixture.Service.Maintain(128, 64, clear: true);
        Assert.Equal(25, result.RemovedCacheBytes);
        foreach (var relative in PersistentFiles) Assert.Equal("preserve:" + relative, File.ReadAllText(Path.Combine(result.ProfilePath, relative)));
        Assert.False(Directory.Exists(Path.Combine(result.ProfilePath, "Default/Cache")));
        Assert.False(Directory.Exists(Path.Combine(result.ProfilePath, "Default/Code Cache")));
    }

    [Fact]
    public void Limits_ClearOnlyOversizedCacheContainersWhileClosed()
    {
        using var fixture = new Fixture();
        fixture.Put(fixture.Service.ProfilePath, "Default/Cache/data", "large");
        using (var file = File.OpenWrite(Path.Combine(fixture.Service.ProfilePath, "Default/Cache/data"))) file.SetLength(17 * 1024 * 1024);
        fixture.Put(fixture.Service.ProfilePath, "Default/Code Cache/data", "small");
        fixture.Put(fixture.Service.ProfilePath, "Default/Network/Cookies", "login");
        var result = fixture.Service.Maintain(16, 16);
        Assert.Equal(17 * 1024 * 1024, result.RemovedCacheBytes);
        Assert.False(Directory.Exists(Path.Combine(result.ProfilePath, "Default/Cache")));
        Assert.True(File.Exists(Path.Combine(result.ProfilePath, "Default/Code Cache/data")));
        Assert.Equal("login", File.ReadAllText(Path.Combine(result.ProfilePath, "Default/Network/Cookies")));
    }

    [WindowsFileFact]
    public async Task CacheLinks_AreRejectedWithoutDeletingOutsideData()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside");
        fixture.Put(outside, "keep", "untouched");
        var cache = Path.Combine(fixture.Service.ProfilePath, "Default/Cache");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Arguments = $"/c mklink /J \"{cache}\" \"{outside}\"";
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Throws<IOException>(() => fixture.Service.Maintain(128, 64, clear: true));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "keep")));
        }
        finally { Directory.Delete(cache); }
    }

    [WindowsFileFact]
    public void ProcessGuard_ParsesExactQuotedProfileArguments()
    {
        const string path = @"C:\Users\Example User\AppData\Local\OpenClaw Companion\browser\managed-profile";
        Assert.True(BrowserProfileProcessGuard.UsesProfile("chrome.exe --type=renderer --user-data-dir=\"" + path + "\"", path));
        Assert.True(BrowserProfileProcessGuard.UsesProfile("chrome.exe --user-data-dir \"" + path.ToUpperInvariant() + "\"", path));
        Assert.False(BrowserProfileProcessGuard.UsesProfile("chrome.exe --user-data-dir=\"" + path + "-other\"", path));
    }

    [Fact]
    public void Settings_CacheDefaultsAndBoundsAreSafe()
    {
        var settings = new CompanionSettings();
        Assert.Equal(128, settings.BrowserDiskCacheMiB);
        Assert.Equal(64, settings.BrowserCodeCacheMiB);
        settings.BrowserDiskCacheMiB = -1; settings.BrowserCodeCacheMiB = int.MaxValue;
        settings.Normalize();
        Assert.Equal(16, settings.BrowserDiskCacheMiB);
        Assert.Equal(2048, settings.BrowserCodeCacheMiB);
    }

    [Fact]
    public void Versions_MatchGatewayWithoutACompanionSuffixAndKeepDiagnosticsSeparate()
    {
        Assert.Equal(BuildInfo.GatewayVersion, BuildInfo.BuildVersion);
        Assert.EndsWith("." + BuildInfo.CompanionRevision, typeof(NodeCommandExecutor).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        var tray = new TrayStatusBroadcaster(buildVersion: BuildInfo.BuildVersion);
        tray.Set(NodeRuntimeState.Connected, "Connected", gatewayVersion: "2026.11.5");
        Assert.Equal("2026.11.5", tray.Current.GatewayVersion);
        Assert.Equal(BuildInfo.BuildVersion, tray.Current.BuildVersion);
        Assert.DoesNotContain("windows", BuildInfo.BuildVersion);
        Assert.Matches(@"^\d{4}\.\d{1,2}\.\d{1,2}", BuildInfo.GatewaySourceVersion);
        Assert.Equal("2026.11.5", GatewayConnection.ReadServerVersion(JsonSerializer.SerializeToElement(new { version = "2026.11.5" })));
        Assert.Null(GatewayConnection.ReadServerVersion(JsonSerializer.SerializeToElement(new { version = "bad\nversion" })));
        Assert.Null(GatewayConnection.ReadServerVersion(JsonSerializer.SerializeToElement(new { version = 5 })));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "OpenClawProfileTests-" + Guid.NewGuid().ToString("N"));
        public BrowserProfileService Service { get; }
        public Fixture(Func<string, bool>? inUse = null) => Service = new(Path.Combine(Root, "app"), Path.Combine(Root, "data"), inUse ?? (_ => false));
        public void Put(string root, string relative, string text) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
