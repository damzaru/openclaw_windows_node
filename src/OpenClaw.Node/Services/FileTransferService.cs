using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Node.Protocol;

namespace OpenClaw.Node.Services;

internal sealed class FileTransferService
{
    internal const int UnaryLimit = 16 * 1024 * 1024;
    internal const int StreamLimit = 50 * 1024 * 1024;
    private const int TreeLimit = 5000, ExpandedTreeLimit = 64 * 1024 * 1024;
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private static readonly SemaphoreSlim TransferCapacity = new(4, 4);

    public async Task<object> ExecuteAsync(string command, string? paramsJson, NodeInvokeContext? context, CancellationToken cancellationToken)
    {
        if (!await TransferCapacity.WaitAsync(0, cancellationToken)) return Error("RESOURCE_EXHAUSTED", "Four filesystem operations are already active; retry after one completes.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(command == "dir.fetch" ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(10));
        var operationToken = deadline.Token;
        try
        {
            using var document = JsonDocument.Parse(paramsJson ?? "{}");
            var p = document.RootElement;
            if (p.ValueKind != JsonValueKind.Object) throw new FileTransferException("INVALID_PARAMS", "Expected a JSON object.");
            operationToken.ThrowIfCancellationRequested();
            return command switch
            {
                "file.stat" => Stat(p),
                "file.fetch" => await FetchAsync(p, context, operationToken),
                "dir.list" => List(p, operationToken),
                "dir.fetch" => await FetchDirectoryAsync(p, operationToken),
                "file.write" => await WriteAsync(p, operationToken),
                "file.create" => await CreateAsync(p, context, operationToken),
                _ => throw new FileTransferException("INVALID_PARAMS", "Unknown filesystem command.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FileTransferException ex) { return new { ok = false, code = ex.Code, message = ex.Message, canonicalPath = ex.CanonicalPath }; }
        catch (UnauthorizedAccessException) { return Error("PERMISSION_DENIED", "Windows denied access to the filesystem target."); }
        catch (FileNotFoundException) { return Error("NOT_FOUND", "Filesystem target was not found."); }
        catch (DirectoryNotFoundException) { return Error("NOT_FOUND", "Filesystem target was not found."); }
        catch (JsonException) { return Error("INVALID_PARAMS", "Invalid filesystem parameters."); }
        catch (OperationCanceledException) { return Error("UNAVAILABLE", "File transfer timed out."); }
        catch (InvalidDataException ex) { return Error("INVALID_PARAMS", ex.Message); }
        catch (FormatException) { return Error("INVALID_BASE64", "Invalid base64 encoding."); }
        catch (IOException) { return Error("READ_ERROR", "Filesystem operation failed; no unverified transfer was published."); }
        finally { TransferCapacity.Release(); }
    }

    private static object Error(string code, string message) => new { ok = false, code, message };
    private static string? Text(JsonElement p, string name) => p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Flag(JsonElement p, string name) => p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string PathParameter(JsonElement p) => WindowsFileLease.ValidatePath(Text(p, "path"));
    private static long Limit(JsonElement p, string name, long fallback, long maximum)
        => p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 1
            ? Math.Min((long)Math.Min(Math.Floor(number), maximum), maximum) : fallback;

    private static WindowsFileLease OpenRead(JsonElement p, bool? directory = null)
    {
        var lease = WindowsFileLease.Open(PathParameter(p), Flag(p, "followSymlinks"));
        try
        {
            CheckCanonical(p, lease.Path);
            CheckExistingBinding(p, lease.Info!.Value);
            if (directory == true && !lease.IsDirectory) throw new FileTransferException("IS_FILE", "Target must be a directory.", lease.Path);
            if (directory == false && lease.IsDirectory) throw new FileTransferException("IS_DIRECTORY", "Target must be a regular file.", lease.Path);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void CheckCanonical(JsonElement p, string canonical)
    {
        if (p.TryGetProperty("expectedCanonicalPath", out var expected) &&
            (expected.ValueKind != JsonValueKind.String || !string.Equals(expected.GetString(), canonical, StringComparison.Ordinal)))
            throw new FileTransferException("CANONICAL_PATH_CHANGED", "Canonical path differs from the authorized target.", canonical);
    }

    private static bool Matches(JsonElement binding, string deviceName, string inodeName, WindowsFileLease.NativeInfo info)
        => Text(binding, deviceName) == info.Device && Text(binding, inodeName) == info.Inode;

    private static void CheckExistingBinding(JsonElement p, WindowsFileLease.NativeInfo info, bool required = false)
    {
        if (!p.TryGetProperty("expectedBinding", out var binding))
        {
            if (required) throw new FileTransferException("CANONICAL_PATH_CHANGED", "Binary fetch requires its authorized existing binding.");
            return;
        }
        if (binding.ValueKind != JsonValueKind.Object || Text(binding, "kind") != "existing" || !Matches(binding, "device", "inode", info))
            throw new FileTransferException("CANONICAL_PATH_CHANGED", "Filesystem identity differs from the authorized target.");
    }

    private static object WriteBinding(WindowsFileLease lease)
    {
        var anchor = lease.AnchorInfo;
        var result = new Dictionary<string, object> { ["kind"] = "write", ["anchorPath"] = lease.AnchorPath, ["anchorDevice"] = anchor.Device, ["anchorInode"] = anchor.Inode };
        if (lease.Info is { } target) { result["targetDevice"] = target.Device; result["targetInode"] = target.Inode; }
        return result;
    }

    private static void CheckWriteBinding(JsonElement p, WindowsFileLease lease, bool createOnly)
    {
        if (!p.TryGetProperty("expectedBinding", out var binding) || binding.ValueKind != JsonValueKind.Object || Text(binding, "kind") != "write")
            throw new FileTransferException("CANONICAL_PATH_CHANGED", "A write requires its authorized write binding.", lease.Path);
        var anchorPath = Text(binding, "anchorPath");
        if (anchorPath == null) throw new FileTransferException("CANONICAL_PATH_CHANGED", "Missing authorized write anchor.");
        using var anchor = WindowsFileLease.Open(anchorPath);
        if (!anchor.IsDirectory || !Matches(binding, "anchorDevice", "anchorInode", anchor.Info!.Value) ||
            !IsWithin(anchor.Path, lease.Path) || string.Equals(anchor.Path, lease.Path, StringComparison.OrdinalIgnoreCase))
            throw new FileTransferException("CANONICAL_PATH_CHANGED", "Authorized write anchor changed or target lies outside it.", lease.Path);
        var hasDevice = binding.TryGetProperty("targetDevice", out _);
        var hasInode = binding.TryGetProperty("targetInode", out _);
        if (hasDevice != hasInode || (hasDevice && (lease.Info is not { } info || !Matches(binding, "targetDevice", "targetInode", info))) ||
            (!createOnly && !hasDevice && lease.Exists))
            throw new FileTransferException("CANONICAL_PATH_CHANGED", "Write target changed after authorization.", lease.Path);
    }

    private static bool IsWithin(string root, string target)
        => string.Equals(root, target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static object Stat(JsonElement p)
    {
        using var lease = OpenRead(p);
        var info = lease.Info!.Value;
        var result = new Dictionary<string, object> { ["ok"] = true, ["path"] = lease.Path, ["type"] = lease.IsDirectory ? "directory" : "file", ["size"] = info.Size, ["mtimeMs"] = info.MtimeMs, ["binding"] = info.Binding };
        if (Flag(p, "preflightOnly")) result["preflightOnly"] = true;
        return result;
    }

    private static async Task<object> FetchAsync(JsonElement p, NodeInvokeContext? context, CancellationToken ct)
    {
        var transport = Text(p, "transport");
        if (p.TryGetProperty("transport", out _) && transport != "binary") return Error("INVALID_PARAMS", "transport must be binary when supplied.");
        var binary = transport == "binary";
        long maxBytes;
        if (binary)
        {
            if (!p.TryGetProperty("maxBytes", out var limit) || limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt64(out maxBytes) || maxBytes < 1 || maxBytes > 9007199254740991)
                return Error("INVALID_PARAMS", "Binary fetch requires a positive safe-integer maxBytes.");
            maxBytes = Math.Min(maxBytes, StreamLimit);
        }
        else maxBytes = Limit(p, "maxBytes", 8 * 1024 * 1024, UnaryLimit);
        using var lease = OpenRead(p, false);
        if (p.TryGetProperty("rootPath", out _) && Text(p, "rootPath") == null) return Error("INVALID_PATH", "rootPath must be an absolute local directory path.");
        if (Text(p, "rootPath") is { } rootPath)
        {
            using var root = WindowsFileLease.Open(rootPath);
            if (!root.IsDirectory || !IsWithin(root.Path, lease.Path)) return Error("INVALID_PATH", "Fetch target is outside rootPath.");
            if ((File.GetAttributes(PathParameter(p)) & FileAttributes.ReparsePoint) != 0)
                return Error("SYMLINK_TARGET_DENIED", "Workspace file reads may not follow a final-file symlink.");
        }
        var info = lease.Info!.Value;
        if (info.Size > maxBytes) return Error("FILE_TOO_LARGE", "File exceeds the admitted byte limit.");
        if (Flag(p, "preflightOnly")) return new { ok = true, path = lease.Path, size = info.Size, mimeType = "", base64 = "", sha256 = "", preflightOnly = true, binding = info.Binding };
        if (binary && context == null) return Error("DUPLEX_REQUIRED", "Binary fetch requires a Gateway duplex invocation.");
        if (binary) CheckExistingBinding(p, info, required: true);
        using var stream = new FileStream(lease.Target!, FileAccess.Read, 64 * 1024, isAsync: false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        var buffer = new byte[binary ? NodeDuplexChannel.MaximumMessageBytes : 64 * 1024];
        using var output = binary ? null : new MemoryStream();
        await using var frames = binary ? new NodeDuplexChannel(context!, ct) : null;
        if (frames != null)
        {
            await frames.StartAsync();
            if (Encoding.UTF8.GetString(await frames.ReceiveAsync()) != "start") throw new InvalidDataException("Expected file.fetch start control.");
        }
        for (;;)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - size + 1)), ct);
            if (read == 0) break;
            size += read;
            if (size > maxBytes) return Error("FILE_TOO_LARGE", "File grew beyond the admitted byte limit.");
            hash.AppendData(buffer, 0, read);
            if (frames != null)
            {
                await frames.SendAsync(buffer.AsMemory(0, read));
                if (Encoding.UTF8.GetString(await frames.ReceiveAsync()) != "ack") throw new InvalidDataException("Expected file.fetch acknowledgement.");
            }
            else output!.Write(buffer, 0, read);
        }
        var content = output?.ToArray();
        var result = new Dictionary<string, object> { ["ok"] = true, ["path"] = lease.Path, ["size"] = size, ["mimeType"] = binary ? "" : DetectMime(lease.Path, content!), ["base64"] = binary ? "" : Convert.ToBase64String(content!), ["sha256"] = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), ["binding"] = info.Binding };
        if (binary) result["transport"] = "binary";
        return result;
    }

    private static object List(JsonElement p, CancellationToken ct)
    {
        using var lease = OpenRead(p, true);
        var info = lease.Info!.Value;
        if (Flag(p, "preflightOnly")) return new { ok = true, path = lease.Path, entries = Array.Empty<object>(), truncated = false, preflight = true, binding = info.Binding };
        var max = (int)Limit(p, "maxEntries", 200, TreeLimit);
        var token = Text(p, "pageToken");
        var offset = token != null && token.All(char.IsAsciiDigit) && int.TryParse(token, out var parsed) ? parsed : 0;
        var names = new List<string>();
        foreach (var child in Directory.EnumerateFileSystemEntries(lease.Path))
        {
            ct.ThrowIfCancellationRequested();
            if (names.Count >= 100000) throw new FileTransferException("TREE_TOO_LARGE", "Directory listing exceeds the bounded 100000-name index.");
            names.Add(child);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var entries = new List<object>();
        foreach (var child in names.Skip(offset).Take(max))
        {
            ct.ThrowIfCancellationRequested();
            using var handle = WindowsFileLease.OpenHandle(child, 0, 3, 3);
            var childInfo = WindowsFileLease.ReadInfo(handle);
            var reparse = (childInfo.Attributes & 0x400) != 0;
            var isDir = !reparse && (childInfo.Attributes & 0x10) != 0;
            var isFile = !reparse && !isDir;
            entries.Add(new { name = Path.GetFileName(child), path = child, size = isFile ? childInfo.Size : 0, mimeType = isDir ? "inode/directory" : Mime(child), isDir, isFile, mtime = childInfo.MtimeMs });
        }
        var truncated = (long)offset + max < names.Count;
        return new { ok = true, path = lease.Path, entries, nextPageToken = truncated ? ((long)offset + max).ToString(System.Globalization.CultureInfo.InvariantCulture) : null, truncated, binding = info.Binding };
    }

    private static async Task<object> FetchDirectoryAsync(JsonElement p, CancellationToken ct)
    {
        using var lease = OpenRead(p, true);
        var maximum = (int)Limit(p, "maxBytes", 8 * 1024 * 1024, UnaryLimit);
        using var bytes = new MemoryStream();
        var entries = new List<string>();
        long expanded = 0;
        using (var capped = new CappedStream(bytes, maximum))
        using (var gzip = new GZipStream(capped, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            async Task Visit(string directory, int depth)
            {
                if (depth > 64) throw new FileTransferException("TREE_TOO_LARGE", "Directory nesting exceeds 64 levels.");
                // Ancestor leases remain alive during descent; every opened member is pinned.
                using var directoryLease = WindowsFileLease.Open(directory);
                var children = new List<string>();
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (entries.Count + children.Count >= TreeLimit) throw new FileTransferException("TREE_TOO_LARGE", "Directory tree exceeds 5000 descendants.");
                    children.Add(child);
                }
                children.Sort(StringComparer.Ordinal);
                foreach (var child in children)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entries.Count >= TreeLimit) throw new FileTransferException("TREE_TOO_LARGE", "Directory tree exceeds 5000 descendants.");
                    using var member = WindowsFileLease.Open(child);
                    var relative = Path.GetRelativePath(lease.Path, member.Path).Replace('\\', '/');
                    if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..") throw new FileTransferException("CANONICAL_PATH_CHANGED", "Archive member escaped the authorized tree.");
                    entries.Add(relative);
                    var metadata = member.Info!.Value;
                    if (metadata.Links > 1 && !member.IsDirectory) throw new FileTransferException("HARDLINK_TARGET_DENIED", "Directory archives do not include hard-linked files.");
                    if (member.IsDirectory)
                    {
                        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, relative + "/"), ct);
                        await Visit(member.Path, depth + 1);
                    }
                    else
                    {
                        expanded += metadata.Size;
                        if (metadata.Size > UnaryLimit || expanded > ExpandedTreeLimit) throw new FileTransferException("TREE_TOO_LARGE", "Archive exceeds 16 MiB per file or 64 MiB expanded limits.");
                        using var content = new FileStream(member.Target!, FileAccess.Read, 64 * 1024, isAsync: false);
                        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, relative) { DataStream = content }, ct);
                    }
                }
            }
            await Visit(lease.Path, 0);
        }
        ct.ThrowIfCancellationRequested();
        entries.Sort(StringComparer.Ordinal);
        var info = lease.Info!.Value;
        if (Flag(p, "preflightOnly")) return new { ok = true, path = lease.Path, tarBase64 = "", tarBytes = 0, sha256 = "", fileCount = entries.Count, entries, preflightOnly = true, binding = info.Binding };
        var archive = bytes.ToArray();
        return new { ok = true, path = lease.Path, tarBase64 = Convert.ToBase64String(archive), tarBytes = archive.Length, sha256 = Digest(archive), fileCount = entries.Count, entries, binding = info.Binding };
    }

    private static byte[] DecodeContent(JsonElement p)
    {
        var value = Text(p, "contentBase64") ?? throw new FileTransferException("INVALID_BASE64", "contentBase64 is required.");
        if (value.Length > ((UnaryLimit + 2L) / 3) * 4) throw new FileTransferException("FILE_TOO_LARGE", "Decoded content exceeds 16 MiB.");
        // Match Gateway's strict padded/unpadded standard or URL-safe encoding contract.
        if (value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '-' or '_' or '=')) || value.Contains('=') && value.TrimEnd('=').Contains('='))
            throw new FileTransferException("INVALID_BASE64", "Invalid base64 alphabet or padding.");
        var normalized = value.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        var padded = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        byte[] bytes;
        try { bytes = Convert.FromBase64String(padded); }
        catch (FormatException) { throw new FileTransferException("INVALID_BASE64", "Invalid base64 encoding."); }
        if (bytes.Length > UnaryLimit) throw new FileTransferException("FILE_TOO_LARGE", "Decoded content exceeds 16 MiB.");
        if (Convert.ToBase64String(bytes).TrimEnd('=') != normalized || value.Length - value.TrimEnd('=').Length > 2 ||
            (value.Contains('=') && value.Length % 4 != 0)) throw new FileTransferException("INVALID_BASE64", "Noncanonical base64 padding bits or length.");
        return bytes;
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void CheckDigest(JsonElement p, string digest)
    {
        if (p.TryGetProperty("expectedSha256", out _) && !string.Equals(Text(p, "expectedSha256"), digest, StringComparison.OrdinalIgnoreCase))
            throw new FileTransferException("INTEGRITY_FAILURE", "Submitted bytes do not match expectedSha256.");
    }

    private static WindowsFileLease OpenWrite(JsonElement p, bool write = false)
    {
        var lease = WindowsFileLease.Open(PathParameter(p), Flag(p, "followSymlinks"), allowMissing: true, write);
        try
        {
            CheckCanonical(p, lease.Path);
            if (lease.IsDirectory) throw new FileTransferException("IS_DIRECTORY", "Target is a directory.", lease.Path);
            if (lease.Info is { Links: > 1 }) throw new FileTransferException("HARDLINK_TARGET_DENIED", "Windows writes refuse hard-linked targets.");
            if (!Directory.Exists(Path.GetDirectoryName(lease.Path)) && !Flag(p, "createParents"))
                throw new FileTransferException("PARENT_NOT_FOUND", "Parent directory does not exist.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static async Task<object> WriteAsync(JsonElement p, CancellationToken ct)
    {
        var content = DecodeContent(p);
        var digest = Digest(content);
        CheckDigest(p, digest);
        await MutationGate.WaitAsync(ct);
        try
        {
            using var lease = OpenWrite(p, write: !Flag(p, "preflightOnly"));
            if (lease.Exists && !Flag(p, "overwrite")) return Error("EXISTS_NO_OVERWRITE", "File already exists and overwrite is false.");
            // The current Gateway requires this acknowledgement for workspace
            // writes. Windows enforces the hard-link restriction for every write.
            if (Flag(p, "preflightOnly")) return new { ok = true, path = lease.Path, size = content.Length, sha256 = digest, overwritten = lease.Exists, binding = WriteBinding(lease), rejectHardlinks = true };
            CheckWriteBinding(p, lease, createOnly: false);
            ct.ThrowIfCancellationRequested();
            if (lease.Exists)
            {
                var info = lease.Info!.Value;
                if (info.Size > UnaryLimit) return Error("FILE_TOO_LARGE", "Existing file exceeds the bounded 16 MiB rollback limit.");
                using var target = new FileStream(lease.Target!, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
                var original = new byte[(int)info.Size];
                await target.ReadExactlyAsync(original, ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    target.Position = 0;
                    await target.WriteAsync(content, ct);
                    target.SetLength(content.Length);
                    target.Flush(flushToDisk: true);
                    ct.ThrowIfCancellationRequested();
                }
                catch
                {
                    target.Position = 0;
                    await target.WriteAsync(original, CancellationToken.None);
                    target.SetLength(original.Length);
                    target.Flush(flushToDisk: true);
                    throw;
                }
                return new { ok = true, path = lease.Path, size = content.Length, sha256 = digest, overwritten = true, binding = info.Binding };
            }
            var receipt = await StageAndPublishAsync(lease, async stream => await stream.WriteAsync(content, ct), ct, allowExisting: false);
            return new { ok = true, path = lease.Path, size = content.Length, sha256 = digest, overwritten = false, binding = receipt.Binding };
        }
        finally { MutationGate.Release(); }
    }

    private static async Task<object> CreateAsync(JsonElement p, NodeInvokeContext? context, CancellationToken ct)
    {
        if (!p.TryGetProperty("sizeBytes", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size) || size < 0 || size > 9007199254740991 ||
            Text(p, "expectedSha256") is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit))
            return Error("INVALID_PARAMS", "file.create requires nonnegative sizeBytes and a 64-hex SHA-256 digest.");
        var max = (long)StreamLimit;
        if (p.TryGetProperty("maxBytes", out var maximum))
        {
            if (maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetInt64(out var requested) || requested < 0 || requested > 9007199254740991) return Error("INVALID_PARAMS", "maxBytes must be a nonnegative safe integer.");
            max = Math.Min(max, requested);
        }
        if (size > max) return Error("FILE_TOO_LARGE", "file.create exceeds the admitted byte limit (maximum 50 MiB).");
        // Do not hold the global mutation gate while waiting for remote input.
        using var lease = OpenWrite(p);
        if (Flag(p, "preflightOnly")) return new { ok = true, path = lease.Path, size, sha256 = digest.ToLowerInvariant(), binding = WriteBinding(lease) };
        CheckWriteBinding(p, lease, createOnly: true);
        if (context == null) return Error("DUPLEX_REQUIRED", "file.create requires binary duplex transport.");
        await using var frames = new NodeDuplexChannel(context, ct);
        // Streaming staging is private and bounded; only a verified complete file is published.
        var received = 0L;
        var receipt = await StageAndPublishAsync(lease, async stream =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await frames.StartAsync();
            for (;;)
            {
                var chunk = await frames.ReceiveAsync();
                if (chunk.Length == 0)
                {
                    if (received != size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), digest, StringComparison.OrdinalIgnoreCase))
                        throw new FileTransferException("INTEGRITY_FAILURE", "file.create content size or digest does not match.");
                    return;
                }
                if (received + chunk.Length > size) throw new FileTransferException("FILE_TOO_LARGE", "file.create chunk exceeds admitted size.");
                received += chunk.Length;
                hash.AppendData(chunk);
                await stream.WriteAsync(chunk, ct);
                await frames.SendAsync(Encoding.UTF8.GetBytes("ack"));
            }
        }, ct, allowExisting: true);
        var result = new Dictionary<string, object> { ["ok"] = true, ["path"] = lease.Path, ["status"] = receipt.Created ? "created" : "exists", ["binding"] = receipt.Binding };
        if (receipt.Created) { result["size"] = size; result["sha256"] = digest.ToLowerInvariant(); }
        return result;
    }

    private sealed record PublishedReceipt(bool Created, object Binding);

    private static async Task<PublishedReceipt> StageAndPublishAsync(WindowsFileLease lease, Func<FileStream, Task> write, CancellationToken ct, bool allowExisting)
    {
        var stagePath = Path.Combine(lease.AnchorPath, ".openclaw-transfer-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var handle = WindowsFileLease.CreateStage(stagePath);
        using var stream = new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
        var published = false;
        try
        {
            await write(stream);
            stream.Flush(flushToDisk: true);
            ct.ThrowIfCancellationRequested();
            if (lease.Exists)
            {
                if (!allowExisting) throw new FileTransferException("CANONICAL_PATH_CHANGED", "File appeared after authorization.");
                return new PublishedReceipt(false, lease.Info!.Value.Binding);
            }
            lease.CreateParents(ct);
            published = WindowsFileLease.PublishStage(handle, lease.Path);
            if (published && !string.Equals(WindowsFileLease.FinalPath(handle), lease.Path, StringComparison.OrdinalIgnoreCase))
            {
                published = false;
                throw new FileTransferException("CANONICAL_PATH_CHANGED", "Published handle does not match the authorized destination.");
            }
            if (!published)
            {
                if (!allowExisting) throw new FileTransferException("CANONICAL_PATH_CHANGED", "File appeared after authorization.");
                using var existing = WindowsFileLease.Open(lease.Path);
                if (existing.IsDirectory) throw new FileTransferException("NOT_FILE", "Create target is not a regular file.");
                return new PublishedReceipt(false, existing.Info!.Value.Binding);
            }
            return new PublishedReceipt(true, WindowsFileLease.ReadInfo(handle).Binding);
        }
        finally { if (!published && !handle.IsClosed) WindowsFileLease.DeleteStage(handle); }
    }

    private static string DetectMime(string path, byte[] content)
    {
        var bytes = content.AsSpan();
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8))
        {
            if (bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
            if (bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) return "audio/wav";
        }
        if (bytes.StartsWith("%PDF-"u8)) return "application/pdf";
        if (bytes.StartsWith(new byte[] { 0x50, 0x4b, 3, 4 })) return "application/zip";
        if (bytes.StartsWith(new byte[] { 0x1f, 0x8b })) return "application/gzip";
        if (bytes.StartsWith("ID3"u8)) return "audio/mpeg";
        if (bytes.StartsWith("OggS"u8)) return "audio/ogg";
        if (bytes.StartsWith("fLaC"u8)) return "audio/flac";
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return "video/mp4";
        var extension = Mime(path);
        if (extension != "application/octet-stream") return extension;
        var length = Math.Min(bytes.Length, 8192);
        if (length > 0 && length < bytes.Length)
        {
            // Complete a UTF-8 sequence cut by the sniff limit, at most 3 bytes.
            var lead = length - 1;
            while (lead > Math.Max(0, length - 4) && (bytes[lead] & 0xc0) == 0x80) lead--;
            var width = bytes[lead] >= 0xf0 ? 4 : bytes[lead] >= 0xe0 ? 3 : bytes[lead] >= 0xc2 ? 2 : 1;
            length = Math.Min(bytes.Length, Math.Max(length, lead + width));
        }
        var sample = bytes[..length];
        if (sample.Length == 0) return "text/plain";
        if (sample.IndexOf((byte)0) >= 0) return extension;
        try { _ = new UTF8Encoding(false, true).GetCharCount(sample); }
        catch (DecoderFallbackException) { return extension; }
        var controls = 0;
        foreach (var b in sample) if (b < 0x20 && b is not (9 or 10 or 13)) controls++;
        return (double)controls / sample.Length < 0.01 ? "text/plain" : extension;
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".md" or ".log" or ".csv" or ".py" or ".cs" or ".ts" or ".js" => "text/plain",
        ".json" => "application/json", ".html" or ".htm" => "text/html", ".xml" => "application/xml",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        ".svg" => "image/svg+xml", ".pdf" => "application/pdf", ".zip" => "application/zip", ".gz" => "application/gzip",
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".mp4" => "video/mp4", _ => "application/octet-stream"
    };

    private sealed class CappedStream(Stream destination, int limit) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_written + buffer.Length > limit) throw new FileTransferException("TREE_TOO_LARGE", "Compressed archive exceeds the admitted byte limit.");
            destination.Write(buffer); _written += buffer.Length;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
