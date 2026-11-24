using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Node.Services;

internal sealed class FileTransferException(string code, string message, string? canonicalPath = null) : IOException(message)
{
    public string Code { get; } = code;
    public string? CanonicalPath { get; } = canonicalPath;
}

/// <summary>
/// Pins every existing directory component against rename/deletion, opens leaves without
/// following reparse points, and derives identity/canonical metadata from those handles.
/// All file operations use local drive paths, never device paths, ADS, or UNC shares.
/// </summary>
internal sealed class WindowsFileLease : IDisposable
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, DeleteAccess = 0x10000;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private readonly List<SafeFileHandle> _handles = new();
    public string Path { get; private set; } = "";
    public string AnchorPath { get; private set; } = "";
    public SafeFileHandle? Target { get; private set; }
    public SafeFileHandle Anchor { get; private set; } = null!;
    public NativeInfo? Info { get; private set; }
    public NativeInfo AnchorInfo => ReadInfo(Anchor);
    public bool Exists => Target != null;
    public bool IsDirectory => Info is { } info && (info.Attributes & 0x10) != 0;

    public static string ValidatePath(string? input)
    {
        if (!OperatingSystem.IsWindows()) throw new FileTransferException("UNAVAILABLE", "Native file transfer requires Windows.");
        if (string.IsNullOrWhiteSpace(input) || input.Length > 32700 || input.Contains('\0') ||
            !System.IO.Path.IsPathFullyQualified(input) || input.Length < 3 || !char.IsAsciiLetter(input[0]) ||
            input[1] != ':' || input[2] is not ('\\' or '/'))
            throw new FileTransferException("INVALID_PATH", "Use an absolute local Windows drive path (for example C:\\Users\\...). UNC and device paths are not supported.");
        var normalized = input.Replace('/', '\\');
        foreach (var part in normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = part.Split('.')[0];
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
                throw new FileTransferException("INVALID_PATH", "Ambiguous, reserved, traversal, or alternate-stream path components are not allowed.");
        }
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(normalized));
    }

    public static WindowsFileLease Open(string input, bool followSymlinks = false, bool allowMissing = false, bool write = false)
    {
        var requested = ValidatePath(input);
        var canonical = ResolveCanonical(requested, allowMissing);
        if (!string.Equals(requested, canonical, StringComparison.OrdinalIgnoreCase) && !followSymlinks)
            throw new FileTransferException("SYMLINK_REDIRECT", "Path traverses an alias. Authorize the canonical path or explicitly allow following symlinks in Gateway policy.", canonical);
        var lease = new WindowsFileLease();
        try
        {
            var root = System.IO.Path.GetPathRoot(canonical)!;
            var components = canonical[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var rootHandle = OpenHandle(current, 0, 3, 3);
            lease.AddDirectory(rootHandle, current);
            for (var i = 0; i < components.Length; i++)
            {
                current = System.IO.Path.Combine(current, components[i]);
                var last = i == components.Length - 1;
                SafeFileHandle handle;
                try { handle = OpenHandle(current, last ? (write ? GenericRead | GenericWrite : GenericRead) : 0, last ? (write ? 0u : 1u) : 3u, 3); }
                catch (FileTransferException ex) when (allowMissing && ex.Code == "NOT_FOUND")
                {
                    lease.Path = System.IO.Path.Combine(lease.AnchorPath, System.IO.Path.Combine(components[i..]));
                    return lease;
                }
                lease._handles.Add(handle);
                var info = ReadInfo(handle);
                if ((info.Attributes & 0x400) != 0)
                    throw new FileTransferException("CANONICAL_PATH_CHANGED", "Path acquired a reparse point while being opened.", canonical);
                var openedPath = FinalPath(handle);
                if (!string.Equals(openedPath, current, StringComparison.OrdinalIgnoreCase))
                    throw new FileTransferException("CANONICAL_PATH_CHANGED", "Opened path differs from the canonical target.", openedPath);
                current = openedPath;
                if (last) { lease.Target = handle; lease.Info = info; lease.Path = openedPath; }
                else
                {
                    if ((info.Attributes & 0x10) == 0) throw new FileTransferException("NOT_FOUND", "A parent component is not a directory.");
                    lease.Anchor = handle; lease.AnchorPath = openedPath;
                }
            }
            if (components.Length == 0) { lease.Target = rootHandle; lease.Info = ReadInfo(rootHandle); lease.Path = FinalPath(rootHandle); }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private void AddDirectory(SafeFileHandle handle, string path)
    {
        _handles.Add(handle);
        var info = ReadInfo(handle);
        if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
            throw new FileTransferException("INVALID_PATH", "Filesystem root is not a regular directory.");
        Anchor = handle; AnchorPath = FinalPath(handle);
    }

    private static string ResolveCanonical(string path, bool allowMissing)
    {
        var suffix = new Stack<string>();
        var candidate = path;
        for (;;)
        {
            try
            {
                using var handle = OpenHandle(candidate, 0, 7, 3, reparse: false);
                var resolved = ValidatePath(FinalPath(handle));
                foreach (var name in suffix) resolved = System.IO.Path.Combine(resolved, name);
                return resolved;
            }
            catch (FileTransferException ex) when (allowMissing && ex.Code == "NOT_FOUND")
            {
                var parent = System.IO.Path.GetDirectoryName(candidate);
                if (parent == null || parent == candidate) throw;
                suffix.Push(System.IO.Path.GetFileName(candidate)); candidate = parent;
            }
        }
    }

    public void CreateParents(CancellationToken cancellationToken)
    {
        var parent = System.IO.Path.GetDirectoryName(Path)!;
        var relative = System.IO.Path.GetRelativePath(AnchorPath, parent);
        if (relative == ".") return;
        var current = AnchorPath;
        foreach (var component in relative.Split('\\'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = System.IO.Path.Combine(current, component);
            if (!CreateDirectoryW(current, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 183) ThrowNative(error);
            }
            var handle = OpenHandle(current, 0, 3, 3);
            _handles.Add(handle);
            var info = ReadInfo(handle);
            if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0 || !string.Equals(FinalPath(handle), current, StringComparison.OrdinalIgnoreCase))
                throw new FileTransferException("CANONICAL_PATH_CHANGED", "New parent is not a regular directory.");
        }
    }

    public static SafeFileHandle CreateStage(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new FileTransferException("UNAVAILABLE", "Windows staging is unavailable.");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new FileTransferException("UNAVAILABLE", "Windows user identity is unavailable.");
        // Owner-only protected DACL: private staging remains private even in a shared
        // destination folder or if the process exits before normal cleanup.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"O:{sid}D:P(A;;FA;;;{sid})", 1, out var descriptor, out _))
            throw new FileTransferException("PERMISSION_DENIED", "Could not establish private staging permissions.");
        var attributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
        try
        {
            Marshal.StructureToPtr(attributes, buffer, false);
            return OpenHandle(path, GenericRead | GenericWrite | DeleteAccess, 0, 1, security: buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); LocalFree(descriptor); }
    }

    public static bool PublishStage(SafeFileHandle handle, string destination)
    {
        var name = Encoding.Unicode.GetBytes(destination);
        // FILE_RENAME_INFO has a trailing WCHAR[1] and 8-byte struct alignment.
        // Include and zero the terminator/padding; some filesystem drivers inspect it.
        var size = 24 + name.Length;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 16, name.Length);
            Marshal.Copy(name, 0, buffer + 20, name.Length);
            if (SetFileInformationByHandle(handle, 3, buffer, (uint)size)) return true;
            var error = Marshal.GetLastWin32Error();
            if (error is 80 or 183) return false;
            ThrowNative(error); return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void DeleteStage(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try { Marshal.WriteInt32(buffer, 1); SetFileInformationByHandle(handle, 4, buffer, 4); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static SafeFileHandle OpenHandle(string path, uint access, uint share, uint creation, bool reparse = true, IntPtr security = default)
    {
        var handle = CreateFileW(path, access, share, security, creation, BackupSemantics | (reparse ? OpenReparsePoint : 0), IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); ThrowNative(error); }
        if (GetFileType(handle) != 1) { handle.Dispose(); throw new FileTransferException("UNSUPPORTED_FILE_TYPE", "Only disk files and directories are supported."); }
        return handle;
    }

    internal static NativeInfo ReadInfo(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowNative(Marshal.GetLastWin32Error());
        // FILE_ID_INFO retains the full 128-bit identity (important on ReFS).
        // A filesystem without stable handle identities is not safe for approved transfers.
        if (!GetFileInformationByHandleEx(handle, 18, out var id, 24) || (id.Low == 0 && id.High == 0))
            throw new FileTransferException("UNAVAILABLE", "Filesystem does not expose a stable Windows file identity.");
        return new NativeInfo(info, id);
    }

    internal static string FinalPath(SafeFileHandle handle)
    {
        var result = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, result, (uint)result.Capacity, 0);
        if (length == 0 || length >= result.Capacity) ThrowNative(Marshal.GetLastWin32Error());
        var path = result.ToString();
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static void ThrowNative(int error) => throw new FileTransferException(error switch
    {
        2 or 3 => "NOT_FOUND", 5 or 32 or 33 => "PERMISSION_DENIED", _ => "READ_ERROR"
    }, new Win32Exception(error).Message);

    public void Dispose() { for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); _handles.Clear(); }

    internal readonly struct NativeInfo(RawInfo info, FileIdInfo id)
    {
        public uint Attributes => info.Attributes;
        public uint Links => info.Links;
        public long Size => ((long)info.SizeHigh << 32) | info.SizeLow;
        public string Device => id.VolumeSerial.ToString(CultureInfo.InvariantCulture);
        public string Inode => id.High.ToString("x16", CultureInfo.InvariantCulture) + id.Low.ToString("x16", CultureInfo.InvariantCulture);
        public double MtimeMs => DateTimeOffset.FromFileTime(((long)(uint)info.WriteTime.dwHighDateTime << 32) | (uint)info.WriteTime.dwLowDateTime).ToUnixTimeMilliseconds();
        public object Binding => new { kind = "existing", device = Device, inode = Inode };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInfo { public ulong VolumeSerial, Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public uint Length; public IntPtr Descriptor; public int InheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out RawInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr info, uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
