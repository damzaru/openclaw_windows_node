using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class WindowsFileFactAttribute : FactAttribute
{
    public WindowsFileFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Native Windows filesystem contract test."; }
}

public sealed class WindowsFileTheoryAttribute : TheoryAttribute
{
    public WindowsFileTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Native Windows filesystem contract test."; }
}
