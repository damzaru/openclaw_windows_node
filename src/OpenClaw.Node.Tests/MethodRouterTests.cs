using System.Text.Json;
using OpenClaw.Node.Protocol;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class MethodRouterTests
    {
        [Fact]
        public void BridgeInvokeResponse_ErrorShape_ShouldSerialize()
        {
            var response = new BridgeInvokeResponse
            {
                Id = "inv-1",
                Ok = false,
                Error = new OpenClawNodeError
                {
                    Code = OpenClawNodeErrorCode.InvalidRequest,
                    Message = "Commands not yet implemented on Windows"
                }
            };

            var json = JsonSerializer.Serialize(response);
            Assert.Contains("\"id\":\"inv-1\"", json);
            Assert.Contains("\"ok\":false", json);
            Assert.Contains("\"message\":\"Commands not yet implemented on Windows\"", json);
        }
    }
}
