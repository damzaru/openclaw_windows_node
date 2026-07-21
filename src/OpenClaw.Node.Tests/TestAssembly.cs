using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestEnvironment
{
    internal static string Directory { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory = Path.Combine(Path.GetTempPath(), "openclaw-windows-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Environment.SetEnvironmentVariable("OPENCLAW_WINDOWS_HOME", Directory);
    }
}
