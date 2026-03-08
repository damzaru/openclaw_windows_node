using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class CoreMethodServiceTests
    {
        [Fact]
        public async Task ConfigPatch_ShouldUpdateHeartbeatsEnabled()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            var req = new RequestFrame
            {
                Params = JsonSerializer.Deserialize<JsonElement>("{\"patch\":{\"heartbeatsEnabled\":false}}")
            };

            await svc.HandleConfigPatchAsync(req);
            var status = await svc.HandleStatusAsync(new RequestFrame());
            var json = JsonSerializer.Serialize(status);

            Assert.Contains("\"heartbeatsEnabled\":false", json);
        }

        [Fact]
        public async Task ChannelsStatus_ShouldReturnOk()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            var result = await svc.HandleChannelsStatusAsync(new RequestFrame());
            var json = JsonSerializer.Serialize(result);

            Assert.Contains("\"ok\":true", json);
            Assert.Contains("\"channels\":[]", json);
        }

        [Fact]
        public async Task DevicePairApprove_ShouldResolvePendingRequest()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            svc.AddPendingPairRequest("req-1", "laptop-a");

            var approveReq = new RequestFrame
            {
                Params = JsonSerializer.Deserialize<JsonElement>("{\"requestId\":\"req-1\"}")
            };

            var approveRes = await svc.HandleDevicePairApproveAsync(approveReq);
            var json = JsonSerializer.Serialize(approveRes);
            Assert.Contains("\"ok\":true", json);
            Assert.Contains("\"status\":\"resolved\"", json);
        }

        [Fact]
        public async Task DevicePairList_ShouldReturnOnlyDevicePendingItems()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            svc.AddPendingPairRequest("req-2", "phone-b", kind: "device");
            svc.AddPendingPairRequest("req-node", "desktop-n", kind: "node");

            var listRes = await svc.HandleDevicePairListAsync(new RequestFrame());
            var json = JsonSerializer.Serialize(listRes);

            Assert.Contains("\"ok\":true", json);
            Assert.Contains("\"requestId\":\"req-2\"", json);
            Assert.Contains("\"deviceLabel\":\"phone-b\"", json);
            Assert.DoesNotContain("req-node", json);
        }

        [Fact]
        public async Task NodePairList_ShouldReturnOnlyNodePendingItems()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            svc.AddPendingPairRequest("req-3", "win-box", kind: "node");
            svc.AddPendingPairRequest("req-dev", "tablet-z", kind: "device");

            var listRes = await svc.HandleNodePairListAsync(new RequestFrame());
            var json = JsonSerializer.Serialize(listRes);

            Assert.Contains("\"ok\":true", json);
            Assert.Contains("\"requestId\":\"req-3\"", json);
            Assert.Contains("\"nodeLabel\":\"win-box\"", json);
            Assert.DoesNotContain("req-dev", json);
        }

        [Fact]
        public async Task HandleGatewayEvent_DevicePairRequested_ShouldPopulateDeviceList()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            var evt = new EventFrame
            {
                Event = "device.pair.requested",
                Payload = JsonSerializer.Deserialize<JsonElement>("{\"requestId\":\"dev-1\",\"displayName\":\"Pixel\"}")
            };

            var handled = svc.HandleGatewayEvent(evt);
            Assert.True(handled);

            var listRes = await svc.HandleDevicePairListAsync(new RequestFrame());
            var json = JsonSerializer.Serialize(listRes);
            Assert.Contains("dev-1", json);
            Assert.Contains("Pixel", json);
        }

        [Fact]
        public async Task HandleGatewayEvent_NodePairRequested_ShouldPopulateNodeList()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            var evt = new EventFrame
            {
                Event = "node.pair.requested",
                Payload = JsonSerializer.Deserialize<JsonElement>("{\"requestId\":\"node-1\",\"displayName\":\"Mac Mini\"}")
            };

            var handled = svc.HandleGatewayEvent(evt);
            Assert.True(handled);

            var listRes = await svc.HandleNodePairListAsync(new RequestFrame());
            var json = JsonSerializer.Serialize(listRes);
            Assert.Contains("node-1", json);
            Assert.Contains("Mac Mini", json);
        }

        [Fact]
        public async Task HandleGatewayEvent_PairResolved_ShouldRemovePendingRequest()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            svc.AddPendingPairRequest("dev-2", "Pixel", kind: "device");

            var resolvedEvt = new EventFrame
            {
                Event = "device.pair.resolved",
                Payload = JsonSerializer.Deserialize<JsonElement>("{\"requestId\":\"dev-2\",\"decision\":\"approved\"}")
            };

            var handled = svc.HandleGatewayEvent(resolvedEvt);
            Assert.True(handled);

            var listRes = await svc.HandleDevicePairListAsync(new RequestFrame());
            var json = JsonSerializer.Serialize(listRes);
            Assert.DoesNotContain("dev-2", json);
        }

        [Fact]
        public async Task ConfigSet_ShouldUpdateHeartbeatsEnabled()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            var req = new RequestFrame
            {
                Params = JsonSerializer.Deserialize<JsonElement>("{\"config\":{\"heartbeatsEnabled\":false}}")
            };

            var res = await svc.HandleConfigSetAsync(req);
            var resJson = JsonSerializer.Serialize(res);
            Assert.Contains("\"ok\":true", resJson);

            var status = await svc.HandleStatusAsync(new RequestFrame());
            var statusJson = JsonSerializer.Serialize(status);
            Assert.Contains("\"heartbeatsEnabled\":false", statusJson);
        }

        [Fact]
        public async Task DevicePairReject_NotFound_ShouldReturnNotFound()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            var req = new RequestFrame
            {
                Params = JsonSerializer.Deserialize<JsonElement>("{\"requestId\":\"missing\"}")
            };

            var res = await svc.HandleDevicePairRejectAsync(req);
            var json = JsonSerializer.Serialize(res);

            Assert.Contains("\"ok\":false", json);
            Assert.Contains("\"status\":\"not-found\"", json);
        }

        [Fact]
        public async Task DevicePairApprove_WithoutRequestId_ShouldReturnValidationError()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            var req = new RequestFrame
            {
                Params = JsonSerializer.Deserialize<JsonElement>("{\"foo\":\"bar\"}")
            };

            var res = await svc.HandleDevicePairApproveAsync(req);
            var json = JsonSerializer.Serialize(res);

            Assert.Contains("\"ok\":false", json);
            Assert.Contains("device.pair.approve requires requestId", json);
        }

        [Fact]
        public async Task NodePairReject_WithoutRequestId_ShouldReturnValidationError()
        {
            var svc = new CoreMethodService(DateTimeOffset.UtcNow);
            var req = new RequestFrame
            {
                Params = JsonSerializer.Deserialize<JsonElement>("{}")
            };

            var res = await svc.HandleNodePairRejectAsync(req);
            var json = JsonSerializer.Serialize(res);

            Assert.Contains("\"ok\":false", json);
            Assert.Contains("node.pair.reject requires requestId", json);
        }

        [Fact]
        public async Task PendingPairRequests_ShouldPersistAndReloadFromDisk()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-core-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var cachePath = Path.Combine(tempDir, "pending-pairs.json");

            try
            {
                var svc1 = new CoreMethodService(DateTimeOffset.UtcNow, cachePath);
                svc1.AddPendingPairRequest("persist-1", "Pixel", "device");

                var svc2 = new CoreMethodService(DateTimeOffset.UtcNow, cachePath);
                var listRes = await svc2.HandleDevicePairListAsync(new RequestFrame());
                var json = JsonSerializer.Serialize(listRes);

                Assert.Contains("persist-1", json);
                Assert.Contains("Pixel", json);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task PairResolve_ShouldPersistRemovalToDisk()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-core-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var cachePath = Path.Combine(tempDir, "pending-pairs.json");

            try
            {
                var svc1 = new CoreMethodService(DateTimeOffset.UtcNow, cachePath);
                svc1.AddPendingPairRequest("persist-2", "DeviceX", "device");

                var resolveReq = new RequestFrame
                {
                    Params = JsonSerializer.Deserialize<JsonElement>("{\"requestId\":\"persist-2\"}")
                };
                _ = await svc1.HandleDevicePairApproveAsync(resolveReq);

                var svc2 = new CoreMethodService(DateTimeOffset.UtcNow, cachePath);
                var listRes = await svc2.HandleDevicePairListAsync(new RequestFrame());
                var json = JsonSerializer.Serialize(listRes);

                Assert.DoesNotContain("persist-2", json);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
