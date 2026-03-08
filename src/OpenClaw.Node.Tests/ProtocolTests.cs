using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Node.Protocol;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class ProtocolTests
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        [Fact]
        public void ConnectParams_ShouldSerializeCorrectly()
        {
            var connectParams = new ConnectParams
            {
                MinProtocol = 3,
                MaxProtocol = 3,
                Role = "node",
                Caps = new List<string> { "screenRecording" },
                Client = new Dictionary<string, object>
                {
                    { "id", "openclaw-macos" },
                    { "platform", "windows" },
                    { "mode", "node" }
                }
            };

            var json = JsonSerializer.Serialize(connectParams, Options);

            Assert.Contains("\"minProtocol\":3", json);
            Assert.Contains("\"maxProtocol\":3", json);
            Assert.Contains("\"role\":\"node\"", json);
            Assert.Contains("\"caps\":[\"screenRecording\"]", json);
            Assert.Contains("\"id\":\"openclaw-macos\"", json);
            Assert.Contains("\"platform\":\"windows\"", json);
            Assert.Contains("\"mode\":\"node\"", json);
        }

        [Fact]
        public void RequestFrame_ShouldDeserializeCorrectly()
        {
            var json = @"{
                ""type"": ""req"",
                ""id"": ""req-123"",
                ""method"": ""status"",
                ""params"": {
                    ""detail"": true
                }
            }";

            var request = JsonSerializer.Deserialize<RequestFrame>(json, Options);

            Assert.NotNull(request);
            Assert.Equal("req", request.Type);
            Assert.Equal("req-123", request.Id);
            Assert.Equal("status", request.Method);
            Assert.NotNull(request.Params);
        }

        [Fact]
        public void EventFrame_ShouldDeserializeCorrectly()
        {
            var json = @"{
                ""type"": ""event"",
                ""event"": ""ping"",
                ""payload"": {
                    ""timestamp"": 1234567890
                }
            }";

            var evt = JsonSerializer.Deserialize<EventFrame>(json, Options);

            Assert.NotNull(evt);
            Assert.Equal("event", evt.Type);
            Assert.Equal("ping", evt.Event);
            Assert.NotNull(evt.Payload);
        }

        [Fact]
        public void ResponseFrame_ShouldSerializeOkPayloadShape()
        {
            var response = new ResponseFrame
            {
                Type = "res",
                Id = "abc123",
                Ok = true,
                Payload = new { status = "online" }
            };

            var json = JsonSerializer.Serialize(response, Options);
            Assert.Contains("\"type\":\"res\"", json);
            Assert.Contains("\"id\":\"abc123\"", json);
            Assert.Contains("\"ok\":true", json);
            Assert.Contains("\"payload\":{\"status\":\"online\"}", json);
        }
    }
}
