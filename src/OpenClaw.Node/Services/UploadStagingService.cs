using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Node.Services;

/// <summary>Private, quota-bounded copies for Gateway-owned uploads. Paths live for 24 hours.</summary>
internal sealed class UploadStagingService : IDisposable
{
    internal const int MaximumTerminalBytes = 16 * 1024 * 1024;
    internal const int MaximumBrowserFileBytes = 10 * 1024 * 1024;
    private const long MaximumRetainedBytes = 256L * 1024 * 1024;
    private const int MaximumDirectories = 64;
    private const string Marker = ".companion-upload";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _root;
    private readonly System.Threading.Timer _cleanup;
    internal static string DefaultRoot => Path.Combine(new SecureStore().BaseDirectory, "uploads");

    internal UploadStagingService(string? root = null)
    {
        _root = Path.GetFullPath(root ?? DefaultRoot);
        _cleanup = new System.Threading.Timer(_ => CleanupBestEffort(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    internal sealed record UploadFile(string Name, string ContentBase64);
    internal sealed record StagedUpload(string Directory, string[] Paths, int[] Sizes);

    internal async Task<StagedUpload> StageAsync(IReadOnlyList<UploadFile> files, bool browser, CancellationToken cancellationToken)
    {
        if (files.Count is < 1 or > 256 || (!browser && files.Count != 1))
            throw new InvalidOperationException("INVALID_REQUEST: invalid upload file count");
        var decoded = new List<byte[]>();
        long total = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(file.Name) || file.Name.Length > 255)
                throw new InvalidOperationException("INVALID_REQUEST: upload name must contain 1 through 255 characters");
            var bytes = DecodeCanonicalBase64(file.ContentBase64, browser ? MaximumBrowserFileBytes : MaximumTerminalBytes);
            total += bytes.Length;
            if (total > MaximumTerminalBytes)
                throw new InvalidOperationException("INVALID_REQUEST: uploads exceed 16 MiB aggregate limit");
            decoded.Add(bytes);
        }
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? directory = null;
        try
        {
            Directory.CreateDirectory(_root);
            RejectReparsePoint(_root);
            // File-share exclusion also serializes separate companion processes.
            using var lease = new FileStream(Path.Combine(_root, ".staging-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CleanupExpired();
            var retained = OwnedDirectories().ToArray();
            var retainedBytes = retained.Sum(MeasureOwnedTree);
            if (retained.Length >= MaximumDirectories || retainedBytes + total > MaximumRetainedBytes)
                throw new InvalidOperationException("RESOURCE_EXHAUSTED: upload staging limit reached; retry after old uploads expire");
            directory = Path.Combine(_root, "upload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, Marker), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            var paths = new string[files.Count];
            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = Path.Combine(directory, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(parent);
                paths[i] = Path.Combine(parent, SanitizeName(files[i].Name));
                using var output = new FileStream(paths[i], FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true);
                await output.WriteAsync(decoded[i], cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new StagedUpload(directory, paths, decoded.Select(bytes => bytes.Length).ToArray());
        }
        catch
        {
            if (directory != null) DiscardDirectory(directory);
            throw;
        }
        finally { Gate.Release(); }
    }

    internal static byte[] DecodeCanonicalBase64(string content, int maxBytes)
    {
        if (content.Length > ((maxBytes + 2L) / 3) * 4 || content.Length % 4 != 0)
            throw new InvalidOperationException("INVALID_REQUEST: invalid or oversized upload encoding");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(content); }
        catch (FormatException) { throw new InvalidOperationException("INVALID_REQUEST: invalid upload encoding"); }
        if (bytes.Length > maxBytes || Convert.ToBase64String(bytes) != content)
            throw new InvalidOperationException("INVALID_REQUEST: upload must use canonical padded base64 within the size limit");
        return bytes;
    }

    internal static string SanitizeName(string name)
    {
        name = name.Split('/', '\\').Last();
        var value = Regex.Replace(name, "[\\x00-\\x1f<>:\"/\\\\|?*!%]", "_").Trim().TrimEnd('.', ' ');
        while (Encoding.UTF8.GetByteCount(value) > 180) value = value[..^1];
        if (value.Length > 0 && char.IsHighSurrogate(value[^1])) value = value[..^1];
        value = value.TrimEnd('.', ' ');
        if (value is "" or "." or "..") value = "upload";
        if (Regex.IsMatch(value.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) value = "_" + value;
        return value;
    }

    internal void Discard(StagedUpload upload) => DiscardDirectory(upload.Directory);

    private IEnumerable<string> OwnedDirectories()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(_root, "upload-*").Where(path =>
            !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) && File.Exists(Path.Combine(path, Marker)));
    }

    private void CleanupExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
        foreach (var directory in OwnedDirectories())
        {
            RejectReparsePoint(Path.Combine(directory, Marker));
            if (long.TryParse(File.ReadAllText(Path.Combine(directory, Marker)), out var created) && created < cutoff)
                DiscardDirectory(directory);
        }
    }

    private void CleanupBestEffort()
    {
        if (!Gate.Wait(0)) return;
        try
        {
            if (!Directory.Exists(_root)) return;
            RejectReparsePoint(_root);
            using var lease = new FileStream(Path.Combine(_root, ".staging-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            CleanupExpired();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { Gate.Release(); }
    }

    private void DiscardDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) != _root || !Path.GetFileName(full).StartsWith("upload-", StringComparison.Ordinal))
            throw new InvalidOperationException("Upload cleanup path is outside its private root");
        if (Directory.Exists(full))
        {
            RejectReparsePoint(full);
            _ = MeasureOwnedTree(full); // Validate every descendant before recursive deletion.
            Directory.Delete(full, recursive: true);
        }
    }

    private static long MeasureOwnedTree(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        long bytes = 0;
        var entries = 0;
        while (pending.TryPop(out var parent))
        {
            RejectReparsePoint(parent);
            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                if (++entries > 4096) throw new IOException("Upload staging directory exceeds its entry limit");
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Upload staging does not follow reparse points");
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(path);
                else bytes = checked(bytes + new FileInfo(path).Length);
            }
        }
        return bytes;
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Upload staging does not follow reparse points");
    }

    public void Dispose() => _cleanup.Dispose();
}
