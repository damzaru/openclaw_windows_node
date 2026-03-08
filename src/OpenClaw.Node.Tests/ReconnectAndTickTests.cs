using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class ReconnectAndTickTests
    {
        [Fact]
        public void GatewayModels_HelloOk_CanDeserialize()
        {
            var json = @"{
                ""policy"": {
                    ""tickIntervalMs"": 15000
                }
            }";

            var opts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var payload = JsonSerializer.Deserialize<HelloOkPayload>(json, opts);
            
            Assert.NotNull(payload);
            Assert.NotNull(payload.Policy);
            Assert.Equal(15000, payload.Policy!.TickIntervalMs);
        }
    }
}