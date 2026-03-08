using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OpenClaw.Node.Protocol
{
    public static class Constants
    {
        public const int GatewayProtocolVersion = 3;
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ErrorCode
    {
        NotLinked,
        NotPaired,
        AgentTimeout,
        InvalidRequest,
        Unavailable
    }

    public class ConnectParams
    {
        [JsonPropertyName("minProtocol")] public int MinProtocol { get; set; }
        [JsonPropertyName("maxProtocol")] public int MaxProtocol { get; set; }
        [JsonPropertyName("client")] public Dictionary<string, object> Client { get; set; } = new();
        [JsonPropertyName("caps")] public List<string>? Caps { get; set; }
        [JsonPropertyName("commands")] public List<string>? Commands { get; set; }
        [JsonPropertyName("permissions")] public Dictionary<string, object>? Permissions { get; set; }
        [JsonPropertyName("pathEnv")] public string? PathEnv { get; set; }
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("scopes")] public List<string>? Scopes { get; set; }
        [JsonPropertyName("device")] public Dictionary<string, object>? Device { get; set; }
        [JsonPropertyName("auth")] public Dictionary<string, object>? Auth { get; set; }
        [JsonPropertyName("locale")] public string? Locale { get; set; }
        [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }
    }

    public class RequestFrame
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "req";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("method")] public string Method { get; set; } = string.Empty;
        [JsonPropertyName("params")] public object? Params { get; set; }
    }

    public class ResponseFrame
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "res";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("payload")] public object? Payload { get; set; }
        [JsonPropertyName("error")] public object? Error { get; set; }
    }

    public class EventFrame
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "event";
        [JsonPropertyName("event")] public string Event { get; set; } = string.Empty;
        [JsonPropertyName("payload")] public object? Payload { get; set; }
    }

    public class HelloOkPayload
    {
        [JsonPropertyName("policy")] public PolicyConfig? Policy { get; set; }
    }

    public class PolicyConfig
    {
        [JsonPropertyName("tickIntervalMs")] public int? TickIntervalMs { get; set; }
    }
}