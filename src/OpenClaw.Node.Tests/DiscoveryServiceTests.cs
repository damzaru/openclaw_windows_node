using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class DiscoveryServiceTests
    {
        [Fact]
        public async Task AnnounceOnceAsync_ShouldEmitExpectedBeaconShape()
        {
            var transport = new FakeDiscoveryTransport();
            var fixedNow = new DateTimeOffset(2026, 2, 23, 19, 30, 0, TimeSpan.Zero);
            var connectParams = BuildConnectParams();

            using var svc = new DiscoveryService(
                connectParams,
                gatewayUrl: "ws://127.0.0.1:18789",
                transport: transport,
                clock: () => fixedNow,
                interval: TimeSpan.FromMinutes(1),
                nextJitterMs: _ => 0,
                enableListener: false);

            await svc.AnnounceOnceAsync(CancellationToken.None);

            Assert.Single(transport.SentPackets);
            var packet = transport.SentPackets[0];
            Assert.Equal("239.255.77.77", packet.Host);
            Assert.Equal(18791, packet.Port);

            using var doc = JsonDocument.Parse(packet.PayloadJson);
            var root = doc.RootElement;

            Assert.Equal("openclaw.node.discovery.v1", root.GetProperty("schema").GetString());
            Assert.Equal("node-host", root.GetProperty("nodeId").GetString());
            Assert.Equal("David-Expert14", root.GetProperty("displayName").GetString());
            Assert.Equal("windows", root.GetProperty("platform").GetString());
            Assert.Equal("node", root.GetProperty("mode").GetString());
            Assert.Equal("dev", root.GetProperty("version").GetString());
            Assert.Equal("2026-02-23T19:30:00.0000000Z", root.GetProperty("announcedAt").GetString());

            var gateway = root.GetProperty("gateway");
            Assert.Equal("ws", gateway.GetProperty("scheme").GetString());
            Assert.Equal("127.0.0.1", gateway.GetProperty("host").GetString());
            Assert.Equal(18789, gateway.GetProperty("port").GetInt32());

            Assert.Contains(root.GetProperty("commands").EnumerateArray(), x => x.GetString() == "screen.record");
            Assert.Contains(root.GetProperty("capabilities").EnumerateArray(), x => x.GetString() == "screenRecording");
        }

        [Fact]
        public async Task Start_ShouldEmitImmediateAndPeriodicAnnouncements()
        {
            var transport = new FakeDiscoveryTransport();
            var connectParams = BuildConnectParams();

            using var cts = new CancellationTokenSource();
            using var svc = new DiscoveryService(
                connectParams,
                gatewayUrl: "ws://127.0.0.1:18789",
                transport: transport,
                interval: TimeSpan.FromMilliseconds(40),
                clock: () => DateTimeOffset.UtcNow,
                nextJitterMs: _ => 0,
                enableListener: false);

            svc.Start(cts.Token);
            await Task.Delay(130);
            await svc.StopAsync();

            Assert.True(transport.SentPackets.Count >= 2, $"Expected at least 2 announcements, got {transport.SentPackets.Count}");
        }

        [Fact]
        public async Task TriggerAnnounceAsync_ShouldThrottleBurstAndAllowAfterGap()
        {
            var transport = new FakeDiscoveryTransport();
            var connectParams = BuildConnectParams();
            var now = new DateTimeOffset(2026, 2, 23, 19, 45, 0, TimeSpan.Zero);

            using var svc = new DiscoveryService(
                connectParams,
                gatewayUrl: "ws://127.0.0.1:18789",
                transport: transport,
                clock: () => now,
                minAnnounceGap: TimeSpan.FromSeconds(2),
                nextJitterMs: _ => 0,
                enableListener: false);

            await svc.TriggerAnnounceAsync("gateway-connected", CancellationToken.None);
            await svc.TriggerAnnounceAsync("network-change", CancellationToken.None);
            Assert.Single(transport.SentPackets);

            now = now.AddSeconds(3);
            await svc.TriggerAnnounceAsync("network-change", CancellationToken.None);
            Assert.Equal(2, transport.SentPackets.Count);
        }

        [Fact]
        public async Task StopAsync_ShouldNotThrow_WhenAnnounceLoopFaults()
        {
            var transport = new FaultingDiscoveryTransport();
            var connectParams = BuildConnectParams();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            using var svc = new DiscoveryService(
                connectParams,
                gatewayUrl: "ws://127.0.0.1:18789",
                transport: transport,
                interval: TimeSpan.FromMilliseconds(30),
                clock: () => DateTimeOffset.UtcNow,
                nextJitterMs: _ => 0,
                enableListener: false);

            svc.Start(cts.Token);
            await Task.Delay(80, cts.Token);

            var ex = await Record.ExceptionAsync(() => svc.StopAsync());
            Assert.Null(ex);
        }

        [Fact]
        public void HandleBeaconJson_ShouldTrackAndExpireEntries()
        {
            var transport = new FakeDiscoveryTransport();
            var connectParams = BuildConnectParams();
            var now = new DateTimeOffset(2026, 2, 23, 20, 0, 0, TimeSpan.Zero);

            using var svc = new DiscoveryService(
                connectParams,
                gatewayUrl: "ws://127.0.0.1:18789",
                transport: transport,
                clock: () => now,
                staleAfter: TimeSpan.FromSeconds(10),
                nextJitterMs: _ => 0,
                enableListener: false);

            const string beacon = """
            {
              "schema": "openclaw.node.discovery.v1",
              "nodeId": "node-remote-1",
              "displayName": "Remote Rig",
              "platform": "windows",
              "version": "dev",
              "instanceId": "remote-instance",
              "gateway": { "scheme": "ws", "host": "10.0.0.2", "port": 18789 },
              "announcedAt": "2026-02-23T20:00:00.0000000Z"
            }
            """;

            svc.HandleBeaconJson(beacon);

            var snapshot = svc.GetKnownNodesSnapshot();
            Assert.Single(snapshot);
            Assert.Equal("node-remote-1", snapshot[0].NodeId);
            Assert.Equal("Remote Rig", snapshot[0].DisplayName);

            now = now.AddSeconds(11);
            var purged = svc.PurgeStaleNodes();

            Assert.Equal(1, purged);
            Assert.Empty(svc.GetKnownNodesSnapshot());
        }

        private static ConnectParams BuildConnectParams()
        {
            return new ConnectParams
            {
                Role = "node",
                Client = new Dictionary<string, object>
                {
                    { "id", "node-host" },
                    { "displayName", "David-Expert14" },
                    { "platform", "windows" },
                    { "mode", "node" },
                    { "version", "dev" },
                    { "instanceId", "instance-123" }
                },
                Caps = new List<string> { "screenRecording", "notifications" },
                Commands = new List<string> { "screen.record", "camera.snap" }
            };
        }

        private sealed class FakeDiscoveryTransport : IDiscoveryTransport
        {
            public List<(string Host, int Port, string PayloadJson)> SentPackets { get; } = new();

            public Task SendAsync(string host, int port, byte[] payload, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SentPackets.Add((host, port, Encoding.UTF8.GetString(payload)));
                return Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }

        private sealed class FaultingDiscoveryTransport : IDiscoveryTransport
        {
            public Task SendAsync(string host, int port, byte[] payload, CancellationToken cancellationToken)
                => throw new InvalidOperationException("simulated transport failure");

            public void Dispose()
            {
            }
        }
    }
}
