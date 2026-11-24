namespace OpenClaw.Node
{
    internal static class BuildInfo
    {
        private static readonly System.Reflection.Assembly Assembly = typeof(BuildInfo).Assembly;
        public static readonly string BuildVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(Assembly)!.InformationalVersion;
        public static readonly string GatewayVersion = Metadata("GatewayVersion");
        public static readonly string GatewaySourceVersion = Metadata("GatewaySourceVersion");
        public static readonly string GatewaySourceCommit = Metadata("GatewaySourceCommit");
        public static readonly string GatewaySyncDate = Metadata("GatewaySyncDate");
        public static readonly string CompanionRevision = Metadata("CompanionRevision");
        public static readonly string BuildId = $"{GatewaySourceCommit[..12]}.{CompanionRevision}";

        private static string Metadata(string key) => System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(Assembly)
            .Single(value => value.Key == key).Value!;
    }
}
