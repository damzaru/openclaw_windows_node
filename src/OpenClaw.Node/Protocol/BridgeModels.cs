using System.Text.Json.Serialization;

namespace OpenClaw.Node.Protocol
{
    public class BridgeInvokeRequest
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "invoke";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
        [JsonPropertyName("paramsJSON")] public string? ParamsJSON { get; set; }
    }

    public class BridgeInvokeResponse
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "invoke-res";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("payloadJSON")] public string? PayloadJSON { get; set; }
        [JsonPropertyName("error")] public OpenClawNodeError? Error { get; set; }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum OpenClawNodeErrorCode
    {
        [JsonPropertyName("NOT_PAIRED")] NotPaired,
        [JsonPropertyName("UNAUTHORIZED")] Unauthorized,
        [JsonPropertyName("NODE_BACKGROUND_UNAVAILABLE")] BackgroundUnavailable,
        [JsonPropertyName("INVALID_REQUEST")] InvalidRequest,
        [JsonPropertyName("UNAVAILABLE")] Unavailable
    }

    public class OpenClawNodeError
    {
        [JsonPropertyName("code")] public OpenClawNodeErrorCode Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
        [JsonPropertyName("retryable")] public bool? Retryable { get; set; }
        [JsonPropertyName("retryAfterMs")] public int? RetryAfterMs { get; set; }
    }
}