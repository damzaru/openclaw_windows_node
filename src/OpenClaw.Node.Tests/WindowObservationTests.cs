using System.Text.Json;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;
public class WindowObservationTests
{
    private const string Scope = "18e81716-0a96-4fcf-8841-7f82c96a1eb8";
    private static readonly WindowIdentity Target = new(98765, 12345, 123, "Fixture", "Same title");
    [Fact]
    public async Task DiscoveryScopesTwoSameTitleWindowsAndRefusesTargetedInput()
    {
        var rows = new[] { new WindowRow(Target, "demo", 2, 3, 50, 20),
            new WindowRow(Target with { Handle = 98766 }, "demo", 60, 3, 50, 20) };
        WindowRead? read = null;
        var inspect = new WindowObservationService((req, _) => {
            read = req;
            return Task.FromResult(req.Window == null ? new WindowReply(rows) :
                new WindowReply(Result: JsonSerializer.SerializeToElement(new { ok = true })));
        });
        using var computer = new ComputerControlService(inspection: inspect, inputEnabled: false);
        var list = JsonSerializer.SerializeToElement(await computer.ActAsync(JsonSerializer.Serialize(new { action = "list_windows", executionId = Scope }), default));
        var windows = list.GetProperty("details").GetProperty("windows");
        var reference = windows[1].GetProperty("windowRef").GetString();
        Assert.NotEqual(windows[0].GetProperty("windowRef").GetString(), reference);
        Assert.DoesNotContain("98766", list.GetRawText());
        string Request(string scope) => JsonSerializer.Serialize(new { action = "get_window_state", windowRef = reference, query = "label", depth = 4, maxElements = 6, includeScreenshot = false, executionId = scope });
        await computer.ActAsync(Request(Scope), default);
        Assert.Equal(new WindowRead(rows[1].Identity, "label", 4, 6, false), read);
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ActAsync(Request(Guid.NewGuid().ToString()), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ActAsync(JsonSerializer.Serialize(new { action = "key", keys = "a", executionId = Scope }), default));
        await computer.ActAsync(JsonSerializer.Serialize(new { action = "__close_execution", executionId = Scope }), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => computer.ActAsync(Request(Scope), default));
    }

    [Fact]
    public async Task DisconnectDuringEnumerationCannotReissueRefs()
    {
        var worker = new TaskCompletionSource<WindowReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspect = new WindowObservationService((_, _) => worker.Task);
        var args = JsonSerializer.SerializeToElement(new { action = "list_windows", executionId = Scope });
        var inflight = inspect.ReadAsync("list_windows", args, Scope, default);
        inspect.Forget(null);
        worker.SetResult(new WindowReply(Array.Empty<WindowRow>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inflight);
    }

    [Fact]
    public void InspectionIsOptInAndDoesNotAdvertiseInput()
    {
        Assert.False(new CompanionSettings().EnableWindowInspection);
        var descriptor = JsonSerializer.SerializeToElement(ComputerControlService.Descriptor("test", true, false));
        Assert.Contains("get_window_state", descriptor.GetProperty("actions").ToString());
        Assert.DoesNotContain("left_click", descriptor.GetProperty("actions").ToString());
        Assert.DoesNotContain("background", descriptor.GetProperty("deliveryModes").ToString());
    }
}
