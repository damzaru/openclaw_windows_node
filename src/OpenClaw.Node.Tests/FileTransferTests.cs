using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

[SupportedOSPlatform("windows")]
public sealed class FileTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OpenClawFileTransferTests-" + Guid.NewGuid().ToString("N"));
    private readonly FileTransferService _service = new();
    public FileTransferTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private string Target(string name = "file.txt") => Path.Combine(_root, name);
    private async Task<JsonElement> Call(string command, object parameters, NodeInvokeContext? context = null, CancellationToken ct = default)
        => JsonSerializer.SerializeToElement(await _service.ExecuteAsync(command, JsonSerializer.Serialize(parameters), context, ct));
    private static void Ok(JsonElement result) => Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
    private static void Error(JsonElement result, string code) { Assert.False(result.GetProperty("ok").GetBoolean()); Assert.Equal(code, result.GetProperty("code").GetString()); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [WindowsFileTheory]
    [InlineData("é", 8191)]
    [InlineData("€", 8190)]
    [InlineData("😀", 8189)]
    public async Task MimeSniff_CompletesUtf8CharacterAtSampleBoundary(string suffix, int prefixLength)
    {
        var path = Target("text.unknown");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(new string('a', prefixLength) + suffix + " tail"));
        var result = await Call("file.fetch", new { path });
        Ok(result);
        Assert.Equal("text/plain", result.GetProperty("mimeType").GetString());
    }
    private static object Bound(string path, JsonElement preflight, params (string Key, object Value)[] extras)
    {
        var p = new Dictionary<string, object> { ["path"] = path, ["expectedCanonicalPath"] = preflight.GetProperty("path").GetString()!, ["expectedBinding"] = preflight.GetProperty("binding") };
        foreach (var extra in extras) p[extra.Key] = extra.Value;
        return p;
    }

    [WindowsFileFact]
    public async Task WorkspaceWritePreflight_AcknowledgesEnforcedHardlinkRejection()
    {
        var preflight = await Call("file.write", new { path = Target(), contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("workspace")), rejectHardlinks = true, preflightOnly = true });
        Ok(preflight);
        Assert.True(preflight.GetProperty("rejectHardlinks").GetBoolean());
        Assert.False(File.Exists(Target()));
    }

    [WindowsFileFact]
    public async Task ReadAndWriteOptInsAreIndependentAndEnforcedByExecutor()
    {
        var defaults = NodeCapabilityRegistry.Build(new());
        foreach (var command in new[] { "file.stat", "file.fetch", "dir.list", "dir.fetch", "file.create", "file.write" }) Assert.DoesNotContain(command, defaults.GatewayCommands);
        var read = NodeCapabilityRegistry.Build(new() { EnableFileRead = true });
        Assert.Contains("file", read.Capabilities);
        foreach (var command in new[] { "file.stat", "file.fetch", "dir.list", "dir.fetch" }) Assert.Contains(command, read.GatewayCommands);
        Assert.DoesNotContain("file.write", read.GatewayCommands);
        var write = NodeCapabilityRegistry.Build(new() { EnableFileWrite = true });
        Assert.Contains("file.write", write.GatewayCommands); Assert.Contains("file.create", write.GatewayCommands); Assert.DoesNotContain("file.fetch", write.GatewayCommands);
        using var disabled = new NodeCommandExecutor(settings: new() { AllowUnrestrictedExecutionPolicy = true });
        var denied = await disabled.ExecuteAsync(new() { Id = "file", Command = "file.fetch", ParamsJSON = JsonSerializer.Serialize(new { path = Target() }) });
        Assert.False(denied.Ok);
        Assert.False(File.Exists(Target()));
        File.WriteAllText(Target(), "hello");
        using var enabled = new NodeCommandExecutor(settings: new() { EnableFileRead = true });
        var accepted = await enabled.ExecuteAsync(new() { Id = "file", Command = "file.fetch", ParamsJSON = JsonSerializer.Serialize(new { path = Target() }) });
        Assert.True(accepted.Ok); Ok(JsonDocument.Parse(accepted.PayloadJSON!).RootElement);
    }

    [WindowsFileTheory]
    [InlineData("file.fetch", "{\"transport\":\"binary\"}", true)]
    [InlineData("file.fetch", "{\"transport\":\"binary\",\"preflightOnly\":true}", false)]
    [InlineData("file.fetch", "{}", false)]
    [InlineData("file.create", "{}", true)]
    [InlineData("file.create", "{\"preflightOnly\":true}", false)]
    [InlineData("file.write", "{}", false)]
    public void StreamInvokesCannotReplayTerminalReceiptWithoutTheirBytes(string command, string json, bool streaming)
        => Assert.Equal(streaming, GatewayConnection.UsesFramedFileTransfer(new() { Command = command, ParamsJSON = json }));

    [WindowsFileFact]
    public async Task StatAndUnaryFetchReturnBoundIdentityDigestAndMetadata()
    {
        var content = Encoding.UTF8.GetBytes("hello ☃\r\n"); File.WriteAllBytes(Target(), content);
        var stat = await Call("file.stat", new { path = Target(), preflightOnly = true }); Ok(stat);
        Assert.Equal("file", stat.GetProperty("type").GetString()); Assert.Equal(content.Length, stat.GetProperty("size").GetInt64());
        Assert.True(stat.GetProperty("mtimeMs").GetDouble() > 0); Assert.Equal("existing", stat.GetProperty("binding").GetProperty("kind").GetString());
        var fetched = await Call("file.fetch", Bound(Target(), stat)); Ok(fetched);
        Assert.Equal(content, Convert.FromBase64String(fetched.GetProperty("base64").GetString()!)); Assert.Equal(Hash(content), fetched.GetProperty("sha256").GetString());
        Assert.Equal("text/plain", fetched.GetProperty("mimeType").GetString());
        Ok(await Call("file.stat", new { path = _root }));
        Error(await Call("file.fetch", new { path = _root }), "IS_DIRECTORY");
        Error(await Call("dir.list", new { path = Target() }), "IS_FILE");
        Error(await Call("file.stat", new { path = Target("absent") }), "NOT_FOUND");
    }

    [WindowsFileTheory]
    [InlineData("relative.txt")]
    [InlineData("C:file.txt")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("\\\\?\\C:\\file.txt")]
    [InlineData("C:\\file.txt:secret")]
    [InlineData("C:\\path\\..\\file.txt")]
    [InlineData("C:\\CON.txt")]
    [InlineData("C:\\LPT¹.txt")]
    [InlineData("C:\\file.txt.")]
    [InlineData("C:\\file.txt ")]
    public async Task UnsafeWindowsPathsAreRejected(string path) => Error(await Call("file.stat", new { path }), "INVALID_PATH");

    [WindowsFileFact]
    public async Task ReplacedReadIdentityAndChangedCanonicalPathAreRejected()
    {
        File.WriteAllText(Target(), "original");
        var preflight = await Call("file.fetch", new { path = Target(), preflightOnly = true }); Ok(preflight);
        File.Move(Target(), Target("original-kept")); File.WriteAllText(Target(), "replacement");
        Error(await Call("file.fetch", Bound(Target(), preflight)), "CANONICAL_PATH_CHANGED");
        Error(await Call("file.stat", new { path = Target(), expectedBinding = new { kind = "existing", device = "bad", inode = "bad" } }), "CANONICAL_PATH_CHANGED");
        Error(await Call("file.fetch", new { path = Target(), expectedCanonicalPath = Target("other") }), "CANONICAL_PATH_CHANGED");
    }

    [WindowsFileFact]
    public async Task FetchDetectsCommonBinarySignaturesAndExtensionlessUtf8Text()
    {
        var path = Target("extensionless"); File.WriteAllText(path, "plain UTF-8 ☃");
        var text = await Call("file.fetch", new { path }); Ok(text); Assert.Equal("text/plain", text.GetProperty("mimeType").GetString());
        File.WriteAllBytes(path, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0 });
        var image = await Call("file.fetch", new { path }); Ok(image); Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        File.WriteAllBytes(path, new byte[] { 1, 0, 2, 255 });
        var binary = await Call("file.fetch", new { path }); Ok(binary); Assert.Equal("application/octet-stream", binary.GetProperty("mimeType").GetString());
    }

    [WindowsFileFact]
    public async Task ReadLimitsAndWorkspaceRootAreEnforced()
    {
        File.WriteAllText(Target(), "123456");
        Error(await Call("file.fetch", new { path = Target(), maxBytes = 5 }), "FILE_TOO_LARGE");
        Ok(await Call("file.fetch", new { path = Target(), rootPath = _root }));
        Directory.CreateDirectory(Target("other"));
        Error(await Call("file.fetch", new { path = Target(), rootPath = Target("other") }), "INVALID_PATH");
        Error(await Call("file.fetch", new { path = Target(), transport = "binary" }), "INVALID_PARAMS");
        Error(await Call("file.fetch", new { path = Target(), transport = "raw" }), "INVALID_PARAMS");
    }

    [WindowsFileFact]
    public async Task DirectoryListingIncludesHiddenFilesAndStablePagination()
    {
        File.WriteAllText(Target(".hidden"), "secret"); File.WriteAllText(Target("z.txt"), "z"); Directory.CreateDirectory(Target("a-dir"));
        var preflight = await Call("dir.list", new { path = _root, preflightOnly = true }); Ok(preflight);
        Assert.Empty(preflight.GetProperty("entries").EnumerateArray()); Assert.True(preflight.GetProperty("preflight").GetBoolean());
        var first = await Call("dir.list", Bound(_root, preflight, ("maxEntries", 2))); Ok(first);
        Assert.Equal(2, first.GetProperty("entries").GetArrayLength()); Assert.True(first.GetProperty("truncated").GetBoolean());
        Assert.Equal(".hidden", first.GetProperty("entries")[0].GetProperty("name").GetString());
        Assert.True(first.GetProperty("entries")[1].GetProperty("isDir").GetBoolean());
        var second = await Call("dir.list", Bound(_root, preflight, ("maxEntries", 2), ("pageToken", first.GetProperty("nextPageToken").GetString()!))); Ok(second);
        Assert.Equal("z.txt", second.GetProperty("entries")[0].GetProperty("name").GetString()); Assert.False(second.GetProperty("truncated").GetBoolean());
    }

    [WindowsFileFact]
    public async Task DirectoryFetchIsBoundedTarGzipWithExactManifestAndDigest()
    {
        File.WriteAllText(Target(".hidden"), "secret"); Directory.CreateDirectory(Target("nested")); File.WriteAllText(Target("nested/Ü.txt"), "unicode");
        var preflight = await Call("dir.fetch", new { path = _root, preflightOnly = true }); Ok(preflight);
        Assert.Equal("", preflight.GetProperty("tarBase64").GetString()); Assert.Equal(3, preflight.GetProperty("fileCount").GetInt32());
        var fetched = await Call("dir.fetch", Bound(_root, preflight)); Ok(fetched);
        var archive = Convert.FromBase64String(fetched.GetProperty("tarBase64").GetString()!);
        Assert.Equal(archive.Length, fetched.GetProperty("tarBytes").GetInt32()); Assert.Equal(Hash(archive), fetched.GetProperty("sha256").GetString());
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress); using var tar = new TarReader(gzip);
        var names = new List<string>(); TarEntry? entry;
        while ((entry = tar.GetNextEntry()) != null) { names.Add(entry.Name.TrimEnd('/')); Assert.True(entry.EntryType is TarEntryType.RegularFile or TarEntryType.Directory); }
        Assert.Equal(fetched.GetProperty("entries").EnumerateArray().Select(e => e.GetString()).Order(), names.Order());
        Error(await Call("dir.fetch", new { path = _root, maxBytes = 32, preflightOnly = true }), "TREE_TOO_LARGE");
        using (var oversized = File.Create(Target("large.bin"))) oversized.SetLength(FileTransferService.UnaryLimit + 1L);
        Error(await Call("dir.fetch", new { path = _root }), "TREE_TOO_LARGE");
    }

    [WindowsFileFact]
    public async Task BoundWritesCreateParentsAndOverwriteWithoutChangingIdentity()
    {
        var path = Target("new/nested/file.txt"); var content = Encoding.UTF8.GetBytes("created"); var encoded = Convert.ToBase64String(content);
        Error(await Call("file.write", new { path, contentBase64 = encoded }), "PARENT_NOT_FOUND");
        var preflight = await Call("file.write", new { path, contentBase64 = encoded, createParents = true, preflightOnly = true }); Ok(preflight);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Error(await Call("file.write", new { path, contentBase64 = encoded, createParents = true }), "CANONICAL_PATH_CHANGED");
        var created = await Call("file.write", Bound(path, preflight, ("contentBase64", encoded), ("createParents", true), ("expectedSha256", Hash(content)))); Ok(created);
        Assert.Equal("created", File.ReadAllText(path)); Assert.False(created.GetProperty("overwritten").GetBoolean());
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.Single(rules);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User, rules[0].IdentityReference); Assert.Equal(AccessControlType.Allow, rules[0].AccessControlType);
        Error(await Call("file.write", new { path, contentBase64 = encoded, preflightOnly = true }), "EXISTS_NO_OVERWRITE");
        var replacement = Convert.ToBase64String(Encoding.UTF8.GetBytes("short"));
        var overwrite = await Call("file.write", new { path, contentBase64 = replacement, overwrite = true, preflightOnly = true }); Ok(overwrite);
        var written = await Call("file.write", Bound(path, overwrite, ("contentBase64", replacement), ("overwrite", true))); Ok(written);
        Assert.Equal("short", File.ReadAllText(path)); Assert.Equal(created.GetProperty("binding").ToString(), written.GetProperty("binding").ToString());
        Assert.Empty(Directory.EnumerateFiles(_root, ".openclaw-transfer-*", SearchOption.AllDirectories));
    }

    [WindowsFileFact]
    public async Task InvalidWriteHashOrIdentityNeverChangesOriginalBytes()
    {
        File.WriteAllText(Target(), "original"); var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("replacement"));
        var preflight = await Call("file.write", new { path = Target(), contentBase64 = encoded, overwrite = true, preflightOnly = true }); Ok(preflight);
        Error(await Call("file.write", Bound(Target(), preflight, ("contentBase64", encoded), ("overwrite", true), ("expectedSha256", new string('0', 64)))), "INTEGRITY_FAILURE");
        Assert.Equal("original", File.ReadAllText(Target()));
        File.Move(Target(), Target("original-kept")); File.WriteAllText(Target(), "changed");
        Error(await Call("file.write", Bound(Target(), preflight, ("contentBase64", encoded), ("overwrite", true))), "CANONICAL_PATH_CHANGED");
        Assert.Equal("changed", File.ReadAllText(Target()));
        Error(await Call("file.write", new { path = Target("new"), contentBase64 = "AB==", preflightOnly = true }), "INVALID_BASE64");
        Error(await Call("file.write", new { path = Target("new"), contentBase64 = "YQ===", preflightOnly = true }), "INVALID_BASE64");
        Ok(await Call("file.write", new { path = Target("new"), contentBase64 = "YQ", preflightOnly = true }));
    }

    [WindowsFileFact]
    public async Task JunctionRedirectsRequireCanonicalAuthorizationAndArchivesNeverFollowThem()
    {
        var outside = Target("outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var tree = Target("tree"); Directory.CreateDirectory(tree); var link = Path.Combine(tree, "link");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = $"/c mklink /J \"{link}\" \"{outside}\"";
        using var process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        try
        {
            var redirected = await Call("file.fetch", new { path = Path.Combine(link, "secret.txt") }); Error(redirected, "SYMLINK_REDIRECT");
            Assert.Equal(Path.Combine(outside, "secret.txt"), redirected.GetProperty("canonicalPath").GetString());
            Ok(await Call("file.fetch", new { path = Path.Combine(link, "secret.txt"), followSymlinks = true }));
            Error(await Call("file.fetch", new { path = Path.Combine(link, "secret.txt"), followSymlinks = true, rootPath = tree }), "INVALID_PATH");
            Error(await Call("dir.fetch", new { path = tree, followSymlinks = true }), "SYMLINK_REDIRECT");
        }
        finally { Directory.Delete(link); }
    }

    [WindowsFileFact]
    public async Task ReplacedWriteAnchorAndMalformedTargetBindingsCannotPublish()
    {
        var anchor = Target("anchor"); Directory.CreateDirectory(anchor); var path = Path.Combine(anchor, "new.txt");
        var preflight = await Call("file.write", new { path, contentBase64 = "YQ==", preflightOnly = true }); Ok(preflight);
        Directory.Move(anchor, Target("kept-anchor")); Directory.CreateDirectory(anchor);
        Error(await Call("file.write", Bound(path, preflight, ("contentBase64", "YQ=="))), "CANONICAL_PATH_CHANGED"); Assert.False(File.Exists(path));
        var current = await Call("file.write", new { path, contentBase64 = "YQ==", preflightOnly = true }); Ok(current);
        var b = current.GetProperty("binding");
        Error(await Call("file.write", new { path, contentBase64 = "YQ==", expectedBinding = new { kind = "write", anchorPath = b.GetProperty("anchorPath").GetString(), anchorDevice = b.GetProperty("anchorDevice").GetString(), anchorInode = b.GetProperty("anchorInode").GetString(), targetDevice = "without-inode" } }), "CANONICAL_PATH_CHANGED");
        File.WriteAllText(path, "appeared");
        Error(await Call("file.write", Bound(path, current, ("contentBase64", "YQ=="), ("overwrite", true))), "CANONICAL_PATH_CHANGED"); Assert.Equal("appeared", File.ReadAllText(path));
    }

    [WindowsFileFact]
    public async Task DirectoryAndStreamAdmissionLimitsApplyDuringPreflight()
    {
        var large = Target("large.bin"); using (var file = File.Create(large)) file.SetLength(FileTransferService.UnaryLimit + 1L);
        Error(await Call("file.fetch", new { path = large, maxBytes = int.MaxValue, preflightOnly = true }), "FILE_TOO_LARGE");
        Error(await Call("file.create", new { path = Target("new"), sizeBytes = FileTransferService.StreamLimit + 1L, expectedSha256 = new string('a', 64), preflightOnly = true }), "FILE_TOO_LARGE");
        Error(await Call("file.create", new { path = Target("new"), sizeBytes = 1, maxBytes = 0, expectedSha256 = new string('a', 64), preflightOnly = true }), "FILE_TOO_LARGE");
        Error(await Call("file.create", new { path = Target("new"), sizeBytes = -1, expectedSha256 = new string('a', 64), preflightOnly = true }), "INVALID_PARAMS");
        var tree = Target("many"); Directory.CreateDirectory(tree);
        for (var i = 0; i <= 5000; i++) File.WriteAllText(Path.Combine(tree, i.ToString()), "");
        Error(await Call("dir.fetch", new { path = tree, preflightOnly = true }), "TREE_TOO_LARGE");
    }

    [WindowsFileFact]
    public async Task HardlinksAreNeverWrittenOrArchived()
    {
        File.WriteAllText(Target(), "original"); Assert.True(CreateHardLinkW(Target("alias"), Target(), IntPtr.Zero));
        Error(await Call("file.write", new { path = Target(), contentBase64 = "YQ==", overwrite = true, preflightOnly = true }), "HARDLINK_TARGET_DENIED");
        Error(await Call("dir.fetch", new { path = _root }), "HARDLINK_TARGET_DENIED");
        Assert.Equal("original", File.ReadAllText(Target("alias"))); Ok(await Call("file.fetch", new { path = Target() }));
    }

    [WindowsFileFact]
    public void OpenHandleLeasePinsAncestorAndFileAgainstReplacement()
    {
        var directory = Target("pinned"); Directory.CreateDirectory(directory); var file = Path.Combine(directory, "file.txt"); File.WriteAllText(file, "pinned");
        using (var lease = WindowsFileLease.Open(file))
        {
            Assert.Throws<IOException>(() => Directory.Move(directory, Target("moved")));
            Assert.Throws<IOException>(() => File.Delete(file));
            Assert.Throws<IOException>(() => File.WriteAllText(file, "race"));
        }
        Directory.Move(directory, Target("moved"));
    }

    [WindowsFileFact]
    public async Task BinaryFetchUsesReadyStartChunksAndPerChunkAcknowledgements()
    {
        var content = RandomNumberGenerator.GetBytes(NodeDuplexChannel.MaximumMessageBytes + 123); File.WriteAllBytes(Target(), content);
        var preflight = await Call("file.fetch", new { path = Target(), transport = "binary", maxBytes = content.Length, preflightOnly = true }); Ok(preflight);
        await using var wire = new DuplexWire();
        var operation = Call("file.fetch", Bound(Target(), preflight, ("transport", "binary"), ("maxBytes", content.Length)), wire.Node, wire.Token);
        await wire.Peer.StartAsync(); await wire.Peer.SendAsync(Encoding.UTF8.GetBytes("start"));
        using var received = new MemoryStream();
        while (received.Length < content.Length) { var chunk = await wire.Peer.ReceiveAsync(); Assert.InRange(chunk.Length, 1, NodeDuplexChannel.MaximumMessageBytes); received.Write(chunk); await wire.Peer.SendAsync(Encoding.UTF8.GetBytes("ack")); }
        var receipt = await operation; Ok(receipt); Assert.Equal(content, received.ToArray()); Assert.Equal(Hash(content), receipt.GetProperty("sha256").GetString()); Assert.Equal("binary", receipt.GetProperty("transport").GetString()); Assert.Equal("", receipt.GetProperty("base64").GetString());
        Assert.All(wire.Progress, frame => Assert.InRange(Encoding.UTF8.GetByteCount(frame), 0, 16 * 1024));
        Assert.Equal("ready", JsonDocument.Parse(wire.Progress[0]).RootElement.GetProperty("kind").GetString());
        Error(await Call("file.fetch", Bound(Target(), preflight, ("transport", "binary"), ("maxBytes", content.Length))), "DUPLEX_REQUIRED");
    }

    [WindowsFileFact]
    public async Task BinaryCreatePublishesOnlyCompleteVerifiedContentAndIsCreateOnly()
    {
        var content = RandomNumberGenerator.GetBytes(NodeDuplexChannel.MaximumMessageBytes + 17); var path = Target("created/nested/binary.bin");
        var preflight = await Call("file.create", new { path, sizeBytes = content.Length, expectedSha256 = Hash(content), createParents = true, preflightOnly = true }); Ok(preflight);
        Assert.False(File.Exists(path)); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        await using (var wire = new DuplexWire())
        {
            var operation = Call("file.create", Bound(path, preflight, ("sizeBytes", content.Length), ("expectedSha256", Hash(content)), ("createParents", true)), wire.Node, wire.Token);
            await wire.Peer.StartAsync();
            await wire.Peer.SendAsync(content.AsMemory(0, NodeDuplexChannel.MaximumMessageBytes)); Assert.Equal("ack", Encoding.UTF8.GetString(await wire.Peer.ReceiveAsync()));
            Assert.False(File.Exists(path)); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
            await wire.Peer.SendAsync(content.AsMemory(NodeDuplexChannel.MaximumMessageBytes)); Assert.Equal("ack", Encoding.UTF8.GetString(await wire.Peer.ReceiveAsync()));
            await wire.Peer.SendAsync(ReadOnlyMemory<byte>.Empty); var receipt = await operation; Ok(receipt); Assert.Equal("created", receipt.GetProperty("status").GetString());
        }
        Assert.True(File.Exists(path), "Created file missing; tree: " + string.Join(", ", Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)));
        Assert.Equal(content, File.ReadAllBytes(path));
        var second = await Call("file.create", new { path, sizeBytes = 0, expectedSha256 = Hash(Array.Empty<byte>()), preflightOnly = true }); Ok(second);
        await using (var wire = new DuplexWire())
        {
            var operation = Call("file.create", Bound(path, second, ("sizeBytes", 0), ("expectedSha256", Hash(Array.Empty<byte>()))), wire.Node, wire.Token);
            await wire.Peer.StartAsync(); await wire.Peer.SendAsync(ReadOnlyMemory<byte>.Empty);
            var receipt = await operation; Ok(receipt); Assert.Equal("exists", receipt.GetProperty("status").GetString()); Assert.False(receipt.TryGetProperty("sha256", out _));
        }
        Assert.Equal(content, File.ReadAllBytes(path)); Assert.Empty(Directory.EnumerateFiles(_root, ".openclaw-transfer-*", SearchOption.AllDirectories));
    }

    [WindowsFileFact]
    public async Task BinaryCreateBadDigestAndCancellationLeaveNoFileOrStaging()
    {
        var path = Target("never-created/target.bin"); var digest = Hash(Encoding.UTF8.GetBytes("right"));
        var preflight = await Call("file.create", new { path, sizeBytes = 5, expectedSha256 = digest, createParents = true, preflightOnly = true }); Ok(preflight);
        var parameters = Bound(path, preflight, ("sizeBytes", 5), ("expectedSha256", digest), ("createParents", true));
        await using (var wire = new DuplexWire())
        {
            var operation = Call("file.create", parameters, wire.Node, wire.Token);
            await wire.Peer.StartAsync(); await wire.Peer.SendAsync(Encoding.UTF8.GetBytes("wrong")); Assert.Equal("ack", Encoding.UTF8.GetString(await wire.Peer.ReceiveAsync())); await wire.Peer.SendAsync(ReadOnlyMemory<byte>.Empty);
            Error(await operation, "INTEGRITY_FAILURE");
        }
        Assert.False(File.Exists(path)); Assert.False(Directory.Exists(Path.GetDirectoryName(path))); Assert.Empty(Directory.EnumerateFiles(_root, ".openclaw-transfer-*"));
        await using (var wire = new DuplexWire())
        {
            var operation = Call("file.create", parameters, wire.Node, wire.Token);
            wire.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        }
        Assert.False(File.Exists(path)); Assert.Empty(Directory.EnumerateFiles(_root, ".openclaw-transfer-*"));
        Error(await Call("file.create", parameters), "DUPLEX_REQUIRED");
    }

    [WindowsFileFact]
    public async Task ConcurrentTransfersAreBoundedAndCancellationReturnsTheirCapacity()
    {
        var wires = new List<DuplexWire>(); var operations = new List<Task<JsonElement>>();
        var parameters = new List<object>();
        for (var i = 0; i < 4; i++)
        {
            var path = Target($"pending-{i}"); var digest = Hash(Array.Empty<byte>());
            var preflight = await Call("file.create", new { path, sizeBytes = 0, expectedSha256 = digest, preflightOnly = true }); Ok(preflight);
            parameters.Add(Bound(path, preflight, ("sizeBytes", 0), ("expectedSha256", digest)));
        }
        try
        {
            foreach (var p in parameters) { var wire = new DuplexWire(); wires.Add(wire); operations.Add(Call("file.create", p, wire.Node, wire.Token)); }
            await Task.WhenAll(wires.Select(w => w.Ready));
            Error(await Call("file.stat", new { path = _root }), "RESOURCE_EXHAUSTED");
            foreach (var wire in wires) wire.Cancel();
            foreach (var operation in operations) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        }
        finally { foreach (var wire in wires) { wire.Cancel(); await wire.DisposeAsync(); } }
        Ok(await Call("file.stat", new { path = _root })); Assert.Empty(Directory.EnumerateFiles(_root, ".openclaw-transfer-*"));
    }

    [WindowsFileTheory]
    [InlineData("{\"v\":2,\"kind\":\"ready\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":1,\"index\":0,\"last\":true,\"data\":\"\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":false,\"data\":\"YQ==\"}")]
    [InlineData("{\"v\":1,\"kind\":\"data\",\"message\":0,\"index\":0,\"last\":true,\"data\":\"AB==\"}")]
    [InlineData("{\"v\":1,\"kind\":\"ready\",\"extra\":true}")]
    [InlineData("{\"v\":1,\"v\":1,\"kind\":\"ready\"}")]
    public async Task MalformedDuplexFramesFailClosed(string frame)
    {
        var input = Channel.CreateUnbounded<NodeInvokeInputPayload>(); input.Writer.TryWrite(new() { PayloadJSON = frame });
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var channel = new NodeDuplexChannel(new NodeInvokeContext(new(), ct.Token, input.Reader, (_, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReceiveAsync());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string link, string target, IntPtr security);

    private sealed class DuplexWire : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
        private readonly Channel<NodeInvokeInputPayload> _nodeInput = Channel.CreateBounded<NodeInvokeInputPayload>(32);
        private readonly Channel<NodeInvokeInputPayload> _peerInput = Channel.CreateBounded<NodeInvokeInputPayload>(32);
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Ready => _ready.Task.WaitAsync(Token);
        public List<string> Progress { get; } = new();
        public NodeInvokeContext Node { get; }
        public NodeDuplexChannel Peer { get; }
        public CancellationToken Token => _cts.Token;
        public DuplexWire()
        {
            Node = new(new(), Token, _nodeInput.Reader, async (frame, ct) => { Progress.Add(frame); await _peerInput.Writer.WriteAsync(new() { PayloadJSON = frame }, ct); if (frame == "{\"v\":1,\"kind\":\"ready\"}") _ready.TrySetResult(); });
            var peer = new NodeInvokeContext(new(), Token, _peerInput.Reader, async (frame, ct) => await _nodeInput.Writer.WriteAsync(new() { PayloadJSON = frame }, ct));
            Peer = new(peer);
        }
        public void Cancel() => _cts.Cancel();
        public async ValueTask DisposeAsync() { await Peer.DisposeAsync(); _cts.Cancel(); _cts.Dispose(); }
    }
}
