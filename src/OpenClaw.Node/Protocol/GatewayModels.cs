using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Node.Protocol
{
    /// <summary>
    /// Wire constants pinned to OpenClaw Gateway 2026.7.2. Native node clients
    /// may negotiate the previous node protocol, while operator clients must
    /// speak the current protocol.
    /// </summary>
    public static class Constants
    {
        public const int GatewayProtocolVersion = 4;
        public const int MinimumNodeProtocolVersion = 3;
        public const int MaximumNodeProtocolVersion = 4;
        public const int MaximumInvokeInputBytes = 16 * 1024;
        public const int MaximumProgressChunkBytes = 16 * 1024;
        public const int DefaultMaximumFrameBytes = 8 * 1024 * 1024;
        public const int DefaultHandshakeTimeoutMs = 15_000;
        public const int DefaultRequestTimeoutMs = 30_000;
    }

    public sealed class GatewayClientInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "node-host";
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; } = "dev";
        [JsonPropertyName("platform")] public string Platform { get; set; } = "windows";
        [JsonPropertyName("deviceFamily")] public string? DeviceFamily { get; set; } = "Windows";
        [JsonPropertyName("modelIdentifier")] public string? ModelIdentifier { get; set; }
        [JsonPropertyName("mode")] public string Mode { get; set; } = "node";
        [JsonPropertyName("instanceId")] public string? InstanceId { get; set; }
    }

    public sealed class GatewayAuthParams
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("deviceToken")] public string? DeviceToken { get; set; }
        [JsonPropertyName("bootstrapToken")] public string? BootstrapToken { get; set; }
        [JsonPropertyName("password")] public string? Password { get; set; }
    }

    public sealed class GatewayDeviceParams
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = string.Empty;
        [JsonPropertyName("signature")] public string Signature { get; set; } = string.Empty;
        [JsonPropertyName("signedAt")] public long SignedAt { get; set; }
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    }

    public class ConnectParams
    {
        [JsonPropertyName("minProtocol")] public int MinProtocol { get; set; } = Constants.MinimumNodeProtocolVersion;
        [JsonPropertyName("maxProtocol")] public int MaxProtocol { get; set; } = Constants.MaximumNodeProtocolVersion;

        // The typed property is the canonical representation. Client remains a
        // dictionary for source compatibility with the original companion and
        // its external tests; GatewayConnection normalizes it before signing.
        [JsonPropertyName("client")] public Dictionary<string, object> Client { get; set; } = new();
        [JsonPropertyName("caps")] public List<string> Caps { get; set; } = new();
        [JsonPropertyName("commands")] public List<string> Commands { get; set; } = new();
        [JsonPropertyName("permissions")] public Dictionary<string, bool> Permissions { get; set; } = new();
        [JsonPropertyName("pathEnv")] public string? PathEnv { get; set; }
        [JsonPropertyName("role")] public string Role { get; set; } = "node";
        [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = new();
        [JsonPropertyName("device")] public GatewayDeviceParams? Device { get; set; }
        [JsonPropertyName("auth")] public GatewayAuthParams? Auth { get; set; }
        [JsonPropertyName("locale")] public string? Locale { get; set; }
        [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }

        public GatewayClientInfo GetClientInfo()
        {
            string StringValue(string key, string fallback)
                => Client.TryGetValue(key, out var value) && value != null
                    ? value.ToString()?.Trim() is { Length: > 0 } text ? text : fallback
                    : fallback;

            string? OptionalStringValue(string key)
                => Client.TryGetValue(key, out var value) && value != null
                    ? value.ToString()?.Trim() is { Length: > 0 } text ? text : null
                    : null;

            return new GatewayClientInfo
            {
                Id = StringValue("id", "node-host"),
                DisplayName = OptionalStringValue("displayName"),
                Version = StringValue("version", "dev"),
                Platform = StringValue("platform", "windows"),
                DeviceFamily = OptionalStringValue("deviceFamily") ?? "Windows",
                ModelIdentifier = OptionalStringValue("modelIdentifier"),
                Mode = StringValue("mode", Role == "node" ? "node" : "ui"),
                InstanceId = OptionalStringValue("instanceId"),
            };
        }
    }

    public class RequestFrame
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "req";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("method")] public string Method { get; set; } = string.Empty;
        [JsonPropertyName("params")] public object? Params { get; set; }
    }

    public sealed class GatewayErrorShape
    {
        [JsonPropertyName("code")] public string Code { get; set; } = "UNAVAILABLE";
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
        [JsonPropertyName("retryable")] public bool? Retryable { get; set; }
        [JsonPropertyName("retryAfterMs")] public int? RetryAfterMs { get; set; }
        [JsonPropertyName("details")] public JsonElement? Details { get; set; }
    }

    public class ResponseFrame
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "res";
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("payload")] public object? Payload { get; set; }
        [JsonPropertyName("error")] public GatewayErrorShape? Error { get; set; }
    }

    public class EventFrame
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "event";
        [JsonPropertyName("event")] public string Event { get; set; } = string.Empty;
        [JsonPropertyName("payload")] public object? Payload { get; set; }
        [JsonPropertyName("seq")] public long? Seq { get; set; }
        [JsonPropertyName("stateVersion")] public long? StateVersion { get; set; }
    }

    public sealed class GatewayFeatureSet
    {
        [JsonPropertyName("methods")] public List<string> Methods { get; set; } = new();
        [JsonPropertyName("events")] public List<string> Events { get; set; } = new();
    }

    public sealed class HelloAuthPayload
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = new();
        [JsonPropertyName("deviceToken")] public string? DeviceToken { get; set; }
        [JsonPropertyName("deviceTokens")] public List<DeviceTokenHandoff> DeviceTokens { get; set; } = new();
    }

    public sealed class DeviceTokenHandoff
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("deviceToken")] public string Token { get; set; } = string.Empty;
        [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = new();
    }

    public class HelloOkPayload
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("protocol")] public int Protocol { get; set; }
        [JsonPropertyName("server")] public JsonElement? Server { get; set; }
        [JsonPropertyName("features")] public GatewayFeatureSet? Features { get; set; }
        [JsonPropertyName("snapshot")] public JsonElement? Snapshot { get; set; }
        [JsonPropertyName("pluginSurfaceUrls")] public Dictionary<string, string> PluginSurfaceUrls { get; set; } = new();
        [JsonPropertyName("auth")] public HelloAuthPayload? Auth { get; set; }
        [JsonPropertyName("policy")] public PolicyConfig? Policy { get; set; }
    }

    public class PolicyConfig
    {
        [JsonPropertyName("tickIntervalMs")] public int? TickIntervalMs { get; set; }
        [JsonPropertyName("maxPayload")] public int? MaxPayload { get; set; }
        [JsonPropertyName("maxBufferedBytes")] public int? MaxBufferedBytes { get; set; }
    }

    public sealed class GatewayRpcException : Exception
    {
        public GatewayRpcException(GatewayErrorShape error)
            : base(string.IsNullOrWhiteSpace(error.Message) ? error.Code : error.Message)
        {
            Error = error;
        }

        public GatewayErrorShape Error { get; }
    }
}
