using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests;

/// <summary>Opt-in proof against the actual validators in a read-only Gateway checkout.</summary>
public sealed class GatewayReferenceContractTests
{
    [GatewayReferenceFact]
    public async Task ComputerWirePayloads_PassCurrentGatewayValidators()
    {
        var gateway = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_REFERENCE")!;
        using var package = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(gateway, "package.json")));
        Assert.Equal(package.RootElement.GetProperty("version").GetString(), BuildInfo.GatewaySourceVersion);
        var validatorPath = Path.Combine(gateway, "src", "plugins", "computer-use-validators.generated.js");
        Assert.True(File.Exists(validatorPath), "Current Gateway computer validators are missing");
        using var service = new ComputerControlService(() => new[] { new DisplayGeometry("synthetic", 0, 0, 100, 100) }, new NoopInput());
        var action = await service.ActAsync("""{"action":"key","keys":"Control_L+a"}""", default);
        var payload = JsonSerializer.Serialize(new
        {
            descriptor = ComputerControlService.Descriptor("reference-check"),
            action,
            snapshot = new { format = "png", base64 = ScreenCaptureService.Png1x1FallbackBase64, displayFrameId = "frame", screenIndex = 0, width = 1, height = 1, capturedAtMs = 1 },
        });
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("OPENCLAW_REFERENCE_NODE") ?? "node")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("""
            import { pathToFileURL } from 'node:url';
            const v = await import(pathToFileURL(process.argv[1]).href);
            let input = ''; for await (const chunk of process.stdin) input += chunk;
            const p = JSON.parse(input);
            for (const [key, validate] of [
              ['descriptor', v.validateComputerUseCapabilityDescriptor],
              ['action', v.validateComputerActResult],
              ['snapshot', v.validateScreenSnapshotResult],
            ]) { if (!validate(p[key])) throw new Error(key + ' rejected by Gateway'); }
            if (!p.descriptor.actions.includes('screenshot')) throw new Error('Gateway would refuse screenshot');
            console.log('Current Gateway accepted descriptor, action result and screenshot payload.');
            """);
        start.ArgumentList.Add(validatorPath);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var output = await process.StandardOutput.ReadToEndAsync();
        var errors = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, output + errors);
    }

    private sealed class NoopInput : IComputerInput
    {
        public void Move(int x, int y) { }
        public void Mouse(string button, bool down) { }
        public void Scroll(int amount, bool horizontal) { }
        public void Key(ushort key, bool down) { }
        public Task TypeAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class GatewayReferenceFactAttribute : FactAttribute
{
    public GatewayReferenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_REFERENCE")))
            Skip = "Set OPENCLAW_GATEWAY_REFERENCE to the read-only Gateway source checkout.";
    }
}
