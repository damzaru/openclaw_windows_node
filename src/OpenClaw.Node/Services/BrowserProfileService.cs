using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenClaw.Node.Services;

internal sealed record BrowserProfileMaintenanceResult(string ProfilePath, bool Migrated, long RemovedCacheBytes, bool BrowserRunning);

/// <summary>Owns profile relocation and offline-only, allowlisted cache maintenance.</summary>
internal sealed class BrowserProfileService
{
    internal static BrowserProfileService Default => new();
    internal string ProfilePath { get; }
    internal string LegacyPath { get; }
    private readonly string _browserRoot;
    private readonly Func<string, bool> _isInUse;

    internal BrowserProfileService(string? appDirectory = null, string? dataDirectory = null, Func<string, bool>? isInUse = null, string? legacyDirectory = null)
    {
        LegacyPath = Path.GetFullPath(legacyDirectory ?? Path.Combine(appDirectory ?? AppContext.BaseDirectory, "runtimes", "browser", "managed-profile"));
        _browserRoot = Path.GetFullPath(Path.Combine(dataDirectory ?? new SecureStore().BaseDirectory, "browser"));
        ProfilePath = Path.Combine(_browserRoot, "managed-profile");
        if (ProfilePath.StartsWith(LegacyPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            LegacyPath.StartsWith(ProfilePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(LegacyPath, ProfilePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Legacy and AppData profiles must be separate, non-nested locations.");
        _isInUse = isInUse ?? BrowserProfileProcessGuard.IsInUse;
    }

    internal BrowserProfileMaintenanceResult Maintain(int diskMiB, int codeMiB, bool clear = false)
    {
        NoLinksInAncestors(_browserRoot);
        Directory.CreateDirectory(_browserRoot);
        var lockPath = Path.Combine(_browserRoot, "profile-maintenance.lock");
        NoLinksInAncestors(lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        NoLinksInAncestors(LegacyPath);
        NoLinksInAncestors(ProfilePath);
        var migrated = false;
        if (Directory.Exists(LegacyPath))
        {
            if (!File.Exists(Path.Combine(LegacyPath, "Local State")))
                throw new InvalidOperationException("The legacy directory is missing Chrome's Local State file. Refusing to move an unrecognized browser profile.");
            if (_isInUse(LegacyPath) || _isInUse(ProfilePath))
                throw new InvalidOperationException("Close the companion-managed browser completely before relocating its profile. No browser data has been changed.");
            if (Directory.Exists(ProfilePath))
            {
                if (Directory.EnumerateFileSystemEntries(ProfilePath).Any())
                    throw new InvalidOperationException("Both legacy and AppData browser profiles contain data. Migration was stopped to avoid overwriting logins or extensions.");
                Directory.Delete(ProfilePath); // Empty destination only; never merge profiles.
            }
            // Same-volume atomic rename preserves every byte, including Local State,
            // cookies, passwords, extensions and their settings. Failure leaves source intact.
            Directory.Move(LegacyPath, ProfilePath);
            migrated = true;
        }
        var running = _isInUse(ProfilePath);
        if (running)
        {
            if (clear) throw new InvalidOperationException("Close the companion-managed browser completely, then choose Clear Browser Cache again. Logins and extensions will be kept.");
            return new(ProfilePath, migrated, 0, true);
        }
        long removed = 0;
        if (Directory.Exists(ProfilePath))
        {
            var candidates = CacheDirectories(Math.Clamp(diskMiB, 16, 2048), Math.Clamp(codeMiB, 16, 2048)).ToArray();
            // Validate the whole plan before deleting anything. Never follow a junction,
            // symlink or another reparse point, including one inside an allowlisted cache.
            var plan = candidates.Where(item => Directory.Exists(item.Path)).Select(item =>
                (item.Path, item.Limit, Size: SafeSize(item.Path))).Where(item => clear || item.Size > item.Limit).ToArray();
            foreach (var item in plan)
            {
                ValidateCachePath(item.Path);
                Directory.Delete(item.Path, recursive: true);
                removed += item.Size;
            }
        }
        return new(ProfilePath, migrated, removed, false);
    }

    private IEnumerable<(string Path, long Limit)> CacheDirectories(int diskMiB, int codeMiB)
    {
        const long MiB = 1024 * 1024;
        foreach (var rootCache in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "DawnGraphiteCache", "DawnWebGPUCache" })
            yield return (Path.Combine(ProfilePath, rootCache), 32 * MiB);
        foreach (var directory in Directory.EnumerateDirectories(ProfilePath))
        {
            var name = Path.GetFileName(directory);
            if (name != "Default" && !System.Text.RegularExpressions.Regex.IsMatch(name, @"^Profile \d+$")) continue;
            NoLinksInAncestors(directory);
            foreach (var cache in new[] { "Cache", "Media Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache" })
                yield return (Path.Combine(directory, cache), (cache == "Code Cache" ? codeMiB : cache is "Cache" or "Media Cache" ? diskMiB : 32) * MiB);
        }
    }

    private long SafeSize(string directory)
    {
        ValidateCachePath(directory);
        long total = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Cache maintenance refused a linked filesystem entry.");
            total += (attributes & FileAttributes.Directory) != 0 ? SafeSize(entry) : new FileInfo(entry).Length;
        }
        return total;
    }

    private void ValidateCachePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(ProfilePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cache path escaped the managed browser profile.");
        NoLinksInAncestors(full);
    }

    private static void NoLinksInAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Browser profile maintenance refuses linked directories or files.");
    }
}

internal static class BrowserProfileProcessGuard
{
    internal static bool IsInUse(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var name in new[] { "chrome", "msedge" })
        foreach (var process in Process.GetProcessesByName(name))
        using (process)
        {
            try
            {
                var commandLine = ReadCommandLine(process.Id);
                if (commandLine == null) { if (!process.HasExited) return true; continue; }
                if (UsesProfile(commandLine, path)) return true;
            }
            catch (InvalidOperationException) { } // Process exited during inspection.
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or ArgumentException)
            { return true; } // Unknown ownership is not permission to move/delete profile data.
        }
        return false;
    }

    internal static bool UsesProfile(string commandLine, string path)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new IOException("Could not inspect browser profile arguments.");
        try
        {
            for (var i = 0; i < count; i++)
            {
                var arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
                var value = arg.StartsWith("--user-data-dir=", StringComparison.Ordinal) ? arg[16..]
                    : arg == "--user-data-dir" && i + 1 < count ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, ++i * IntPtr.Size)) : null;
                if (value != null && string.Equals(Path.GetFullPath(value).TrimEnd('\\', '/'), Path.GetFullPath(path).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { LocalFree(argv); }
    }

    private static string? ReadCommandLine(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var size);
            if (size < 16 || size > 1024 * 1024) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(handle, 60, buffer, size, out _) != 0) return null;
                var length = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                if (text.ToInt64() < buffer.ToInt64() || text.ToInt64() + length > buffer.ToInt64() + size || length % 2 != 0) return null;
                return Marshal.PtrToStringUni(text, length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
