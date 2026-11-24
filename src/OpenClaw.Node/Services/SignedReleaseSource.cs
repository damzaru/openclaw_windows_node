using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sigstore.Verification;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace OpenClaw.Node.Services;

// Only an exact main-branch release workflow can authorize a package. No user token,
// caller-supplied URL, or unsigned/delta fallback is used by installed companions.
internal sealed class SignedReleaseSource : IUpdateSource
{
    private static string Repository => RepositoryUrl(BuildInfo.ReleaseRepository);
    internal static string RepositoryUrl(string slug) =>
        Regex.IsMatch(slug, @"\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_-][A-Za-z0-9_.-]*\z")
            ? "https://github.com/" + slug
            : throw new InvalidDataException("This build has no valid release repository.");
    private static string Signer => Repository + "/.github/workflows/windows-release.yml@refs/heads/main";
    internal const string PackageId = "OpenClawCompanion";
    private const long PackageLimit = 512L * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(8) };
    private static readonly Lazy<ServiceProvider> Services = new(() => new ServiceCollection().AddLogging().AddSigstore().BuildServiceProvider());
    private readonly CancellationToken _lifetime;
    public SignedReleaseSource(CancellationToken lifetime = default) => _lifetime = lifetime;

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var feed = Repository + "/releases/latest/download/releases.win.json";
        var bytes = await ReadMetadata(feed, _lifetime);
        var bundle = Encoding.UTF8.GetString(await ReadMetadata(feed + ".sigstore.json", _lifetime));
        return await VerifyFeedAsync(bytes, bundle, _lifetime);
    }

    internal static async Task<VelopackAssetFeed> VerifyFeedAsync(byte[] bytes, string bundle, CancellationToken token)
    {
        var signature = await Services.Value.GetRequiredService<Verifier>().VerifyAsync(bundle, bytes,
            VerificationPolicy.ForExact("https://token.actions.githubusercontent.com", Signer), token);
        if (!signature.IsSuccess) throw new InvalidDataException("Release signature verification failed.");
        var result = VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(bytes));
        if (result.Assets.Length != 1) throw new InvalidDataException("Expected one full Windows package.");
        Validate(result.Assets[0]);
        return result;
    }

    internal static void Validate(VelopackAsset asset)
    {
        if (asset.PackageId != PackageId || asset.Type != VelopackAssetType.Full || asset.Version == null ||
            !Regex.IsMatch(asset.Version.ToString(), @"\A1\.0\.[1-9][0-9]*\z") ||
            asset.FileName != $"{PackageId}-{asset.Version}-full.nupkg" ||
            asset.Size is <= 0 or > PackageLimit || !Regex.IsMatch(asset.SHA256 ?? "", @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("Invalid Windows update package.");
    }

    private static async Task<byte[]> ReadMetadata(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (data.Length + count > 1024 * 1024) throw new InvalidDataException("Update metadata too large.");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default)
    {
        Validate(releaseEntry);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelToken, _lifetime);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = deadline.Token;
        var url = $"{Repository}/releases/download/windows-{releaseEntry.Version}/{releaseEntry.FileName}";
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var disk = File.Create(localFile);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if ((total += count) > releaseEntry.Size) throw new InvalidDataException("Update exceeds signed size.");
            hash.AppendData(buffer, 0, count);
            await disk.WriteAsync(buffer.AsMemory(0, count), ct);
            progress((int)(100 * total / releaseEntry.Size));
        }
        if (total != releaseEntry.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(releaseEntry.SHA256)))
            throw new InvalidDataException("Update did not match its signed digest.");
    }
}
