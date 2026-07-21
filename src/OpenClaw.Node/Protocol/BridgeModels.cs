using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Node.Protocol
{
    public class BridgeInvokeRequest
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "invoke";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("nodeId")] public string NodeId { get; set; } = string.Empty;
        [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
        [JsonPropertyName("paramsJSON")] public string? ParamsJSON { get; set; }
        [JsonPropertyName("timeoutMs")] public int? TimeoutMs { get; set; }
        [JsonPropertyName("idempotencyKey")] public string? IdempotencyKey { get; set; }
    }

    public class BridgeInvokeResponse
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "invoke-res";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("nodeId")] public string? NodeId { get; set; }
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("payload")] public object? Payload { get; set; }
        [JsonPropertyName("payloadJSON")] public string? PayloadJSON { get; set; }
        [JsonPropertyName("error")] public OpenClawNodeError? Error { get; set; }
    }

    public sealed class NodeInvokeCancelPayload
    {
        [JsonPropertyName("invokeId")] public string InvokeId { get; set; } = string.Empty;
        [JsonPropertyName("nodeId")] public string NodeId { get; set; } = string.Empty;
    }

    public sealed class NodeInvokeInputPayload
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("nodeId")] public string NodeId { get; set; } = string.Empty;
        [JsonPropertyName("seq")] public long Seq { get; set; }
        [JsonPropertyName("payloadJSON")] public string PayloadJSON { get; set; } = string.Empty;
    }

    public sealed class NodeInvokeProgressParams
    {
        [JsonPropertyName("invokeId")] public string InvokeId { get; set; } = string.Empty;
        [JsonPropertyName("nodeId")] public string NodeId { get; set; } = string.Empty;
        [JsonPropertyName("seq")] public long Seq { get; set; }
        [JsonPropertyName("chunk")] public string Chunk { get; set; } = string.Empty;
    }

    [JsonConverter(typeof(OpenClawNodeErrorCodeConverter))]
    public enum OpenClawNodeErrorCode
    {
        NotPaired,
        Unauthorized,
        BackgroundUnavailable,
        InvalidRequest,
        Unavailable,
        Timeout,
        SystemRunDenied,
        Cancelled,
        MicPermissionRequired,
        MicBusy,
        PttBusy,
    }

    public sealed class OpenClawNodeErrorCodeConverter : JsonConverter<OpenClawNodeErrorCode>
    {
        public override OpenClawNodeErrorCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.GetString() ?? string.Empty;
            return raw switch
            {
                "NOT_PAIRED" => OpenClawNodeErrorCode.NotPaired,
                "UNAUTHORIZED" => OpenClawNodeErrorCode.Unauthorized,
                "NODE_BACKGROUND_UNAVAILABLE" => OpenClawNodeErrorCode.BackgroundUnavailable,
                "INVALID_REQUEST" => OpenClawNodeErrorCode.InvalidRequest,
                "TIMEOUT" => OpenClawNodeErrorCode.Timeout,
                "SYSTEM_RUN_DENIED" => OpenClawNodeErrorCode.SystemRunDenied,
                "CANCELLED" => OpenClawNodeErrorCode.Cancelled,
                "MIC_PERMISSION_REQUIRED" => OpenClawNodeErrorCode.MicPermissionRequired,
                "MIC_BUSY" => OpenClawNodeErrorCode.MicBusy,
                "PTT_BUSY" => OpenClawNodeErrorCode.PttBusy,
                _ => OpenClawNodeErrorCode.Unavailable,
            };
        }

        public override void Write(Utf8JsonWriter writer, OpenClawNodeErrorCode value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                OpenClawNodeErrorCode.NotPaired => "NOT_PAIRED",
                OpenClawNodeErrorCode.Unauthorized => "UNAUTHORIZED",
                OpenClawNodeErrorCode.BackgroundUnavailable => "NODE_BACKGROUND_UNAVAILABLE",
                OpenClawNodeErrorCode.InvalidRequest => "INVALID_REQUEST",
                OpenClawNodeErrorCode.Timeout => "TIMEOUT",
                OpenClawNodeErrorCode.SystemRunDenied => "SYSTEM_RUN_DENIED",
                OpenClawNodeErrorCode.Cancelled => "CANCELLED",
                OpenClawNodeErrorCode.MicPermissionRequired => "MIC_PERMISSION_REQUIRED",
                OpenClawNodeErrorCode.MicBusy => "MIC_BUSY",
                OpenClawNodeErrorCode.PttBusy => "PTT_BUSY",
                _ => "UNAVAILABLE",
            });
        }
    }

    public class OpenClawNodeError
    {
        [JsonPropertyName("code")] public OpenClawNodeErrorCode Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
        [JsonPropertyName("retryable")] public bool? Retryable { get; set; }
        [JsonPropertyName("retryAfterMs")] public int? RetryAfterMs { get; set; }
        [JsonPropertyName("details")] public JsonElement? Details { get; set; }
    }
}
