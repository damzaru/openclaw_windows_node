using System.Reflection;
using System.Text.Json;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class GatewayOctoberParityTests
{
    private const string Execution = "123e4567-e89b-42d3-a456-426614174000";
    private const string OtherExecution = "123e4567-e89b-42d3-a456-426614174001";

    [Fact]
    public void ComputerDescriptor_ExposesScreenshotToCapabilityDrivenGateway()
    {
        var descriptor = JsonSerializer.SerializeToElement(ComputerControlService.Descriptor("generation"));
        var actions = descriptor.GetProperty("actions").EnumerateArray().Select(value => value.GetString()).ToArray();
        // Current Gateway availableComputerActions derives wait from screenshot,
        // and refuses screenshot itself when the descriptor omits it.
        Assert.Contains("screenshot", actions);
        Assert.DoesNotContain("__close_execution", actions);
        Assert.Contains("computer.act", NodeCapabilityRegistry.Build(new() { EnableComputerControl = true }).GatewayCommands);
        Assert.Contains("screen.snapshot", NodeCapabilityRegistry.Build(new() { EnableComputerControl = true }).GatewayCommands);
    }

    [Fact]
    public async Task ExecutionClose_ReleasesItsSplitDragAndInvalidatesItsFrames()
    {
        var input = new RecordingInput();
        using var service = CreateComputer(input);
        var frame = service.RegisterFrame(0, 100, 100, service.CurrentTopology(), Execution);
        await service.ActAsync(JsonSerializer.Serialize(new { action = "left_mouse_down", executionId = Execution, modifiers = "CTRL" }), default);
        input.Events.Clear();
        var result = JsonSerializer.SerializeToElement(await service.ActAsync(JsonSerializer.Serialize(new { action = "__close_execution", executionId = Execution, reason = "execution-complete" }), default));
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { "mouse:left:False", "key:17:False" }, input.Events);
        input.Events.Clear();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActAsync(JsonSerializer.Serialize(new
        {
            action = "mouse_move", executionId = Execution, displayFrameId = frame, refWidth = 100, x = 10, y = 10,
        }), default));
        Assert.StartsWith("COMPUTER_STALE_OBSERVATION", error.Message);
        Assert.Empty(input.Events);
        await service.ActAsync(JsonSerializer.Serialize(new { action = "__close_execution", executionId = Execution, reason = "execution-complete" }), default);
    }

    [Fact]
    public async Task ExecutionClose_DoesNotReleaseAnotherExecutionsHeldInput()
    {
        var input = new RecordingInput();
        using var service = CreateComputer(input);
        await service.ActAsync(JsonSerializer.Serialize(new { action = "left_mouse_down", executionId = Execution }), default);
        input.Events.Clear();
        await service.ActAsync(JsonSerializer.Serialize(new { action = "__close_execution", executionId = OtherExecution, reason = "execution-complete" }), default);
        Assert.Empty(input.Events);
        await service.ActAsync(JsonSerializer.Serialize(new { action = "left_mouse_up", executionId = Execution }), default);
        Assert.Equal(new[] { "mouse:left:False" }, input.Events);
    }

    [Fact]
    public async Task Frames_CannotBeBorrowedByAnotherExecution()
    {
        var input = new RecordingInput();
        using var service = CreateComputer(input);
        var frame = service.RegisterFrame(0, 100, 100, service.CurrentTopology(), Execution);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActAsync(JsonSerializer.Serialize(new
        {
            action = "left_click", executionId = OtherExecution, displayFrameId = frame, refWidth = 100, x = 10, y = 10,
        }), default));
        Assert.StartsWith("COMPUTER_STALE_OBSERVATION", error.Message);
        Assert.Empty(input.Events);
    }

    [Theory]
    [InlineData("ctrl+a", 0x11, 0x41)]
    [InlineData("Control_L+Return", 0xA2, 0x0D)]
    [InlineData("Shift_R+Insert", 0xA1, 0x2D)]
    [InlineData("cmd+c", 0x5B, 0x43)]
    [InlineData("CTRL+PLUS", 0x11, 0xBB)]
    public void KeyboardChords_AcceptGatewayAndNativeProviderVocabulary(string chord, int first, int second)
        => Assert.Equal(new[] { (ushort)first, (ushort)second }, ComputerControlService.ParseKeys(chord));

    [Theory]
    [InlineData("left", true, false, 2u)]
    [InlineData("left", false, false, 4u)]
    [InlineData("right", true, false, 8u)]
    [InlineData("right", false, false, 16u)]
    [InlineData("left", true, true, 8u)]
    [InlineData("left", false, true, 16u)]
    [InlineData("right", true, true, 2u)]
    [InlineData("right", false, true, 4u)]
    [InlineData("middle", true, true, 32u)]
    [InlineData("middle", false, true, 64u)]
    public void MouseActions_RespectPrimaryButtonHandedness(string button, bool down, bool swapped, uint expected)
        => Assert.Equal(expected, WindowsComputerInput.MouseFlags(button, down, swapped));

    [Fact]
    public void ProductVersion_MatchesGatewayDateVersionAndAssemblyMetadata()
    {
        Assert.Matches("^\\d{4}\\.\\d{1,2}\\.\\d{1,2}(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", BuildInfo.BuildVersion);
        var assembly = typeof(NodeCommandExecutor).Assembly;
        Assert.Equal(BuildInfo.BuildVersion, assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        Assert.Equal(new Version(BuildInfo.BuildVersion.Split('-', '+')[0] + ".0"), assembly.GetName().Version);
        Assert.Matches("^[0-9a-f]{40}$", BuildInfo.GatewaySourceCommit);
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", BuildInfo.GatewaySyncDate);
    }

    private static ComputerControlService CreateComputer(RecordingInput input)
        => new(() => new[] { new DisplayGeometry("display", -200, 0, 200, 200) }, input);

    private sealed class RecordingInput : IComputerInput
    {
        public List<string> Events { get; } = new();
        public void Move(int x, int y) => Events.Add($"move:{x},{y}");
        public void Mouse(string button, bool down) => Events.Add($"mouse:{button}:{down}");
        public void Scroll(int amount, bool horizontal) => Events.Add($"scroll:{amount}:{horizontal}");
        public void Key(ushort key, bool down) => Events.Add($"key:{key}:{down}");
        public Task TypeAsync(string text, CancellationToken cancellationToken) { Events.Add("type:" + text); return Task.CompletedTask; }
    }
}
