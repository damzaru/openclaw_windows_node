using OpenClaw.Node.Services;
using OpenClaw.Node.Protocol;
using Xunit;

namespace OpenClaw.Node.Tests;

public sealed class CompanionLogTests
{
    [Theory]
    [InlineData("authToken=secret-value")]
    [InlineData("\"deviceToken\":\"secret-value\"")]
    [InlineData("password='secret-value'")]
    [InlineData("Authorization: Bearer secret-value")]
    [InlineData("Authorization: Basic secret-value")]
    [InlineData("wss://user:secret-value@gateway.example/mount?token=secret-value#secret-value")]
    [InlineData("https://gateway.example/__openclaw__/cap/secret-value/canvas/")]
    public void Redaction_RemovesCredentialFieldsAndCapabilityUrls(string text)
    {
        var safe = CompanionLog.Redact(text);
        Assert.DoesNotContain("secret-value", safe);
        Assert.Contains("redacted", safe);
    }

    [Fact]
    public void ConnectionDiagnostics_PreserveNestedTransportCauseAndHttpStatus()
    {
        var error = new System.Net.WebSockets.WebSocketException("Unable to connect", new HttpRequestException("Handshake rejected", new System.Net.Sockets.SocketException(10061), System.Net.HttpStatusCode.Forbidden));
        var message = GatewayConnection.DescribeConnectionException(error);
        Assert.Contains("HTTP=403", message);
        Assert.Contains("socket=ConnectionRefused", message);
        Assert.Contains("WebSocketException", message);
    }

    [Fact]
    public void CertificateDiagnostics_DoNotRelaxDefaultValidationOrConfiguredPins()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=gateway.example", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(GatewayConnection.ValidateServerCertificate(certificate, System.Net.Security.SslPolicyErrors.None, ""));
        Assert.False(GatewayConnection.ValidateServerCertificate(certificate, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, ""));
        Assert.False(GatewayConnection.ValidateServerCertificate(certificate, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, ""));
        var pin = Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
        Assert.True(GatewayConnection.ValidateServerCertificate(certificate, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch, pin));
        Assert.False(GatewayConnection.ValidateServerCertificate(certificate, System.Net.Security.SslPolicyErrors.None, new string('0', 64)));
    }

    [Fact]
    public void Session_PersistsOutputErrorsAndPartialLinesWithKnownSecretsRemoved()
    {
        var directory = Path.Combine(TestEnvironment.Directory, "log-" + Guid.NewGuid().ToString("N"));
        var console = new StringWriter();
        var errors = new StringWriter();
        string path;
        CompanionLog.RegisterSecret("unstructured-fixture-credential");
        using (var session = new CompanionLog.Session(directory, console, errors))
        {
            session.Output.Write("Connecting: ");
            session.Output.WriteLine("unstructured-fixture-credential");
            session.Error.WriteLine("Gateway rejected the connection");
            session.Output.Write("Last partial line");
            path = session.CurrentPath;
        }
        var content = File.ReadAllText(path);
        Assert.Contains("[INFO] Connecting: [redacted]", content);
        Assert.Contains("[ERROR] Gateway rejected the connection", content);
        Assert.Contains("Last partial line", content);
        Assert.DoesNotContain("unstructured-fixture-credential", content);
        Assert.DoesNotContain("unstructured-fixture-credential", console.ToString());
    }

    [Fact]
    public void Session_RotatesAndRetainsAtMostTenClosedLogs()
    {
        var directory = Path.Combine(TestEnvironment.Directory, "log-rotation-" + Guid.NewGuid().ToString("N"));
        using (var session = new CompanionLog.Session(directory, TextWriter.Null, TextWriter.Null, maximumBytes: 128))
            for (var i = 0; i < 25; i++) session.Output.WriteLine("rotation " + i + new string('x', 100));
        var files = Directory.GetFiles(directory, "companion-*.log");
        Assert.InRange(files.Length, 2, 10);
        Assert.Contains(files, path => File.ReadAllText(path).Contains("rotation 24"));
    }

    [Fact]
    public void Session_OmitsOversizedLinesRatherThanLeakingSplitCredentials()
    {
        var directory = Path.Combine(TestEnvironment.Directory, "log-size-" + Guid.NewGuid().ToString("N"));
        string path;
        using (var session = new CompanionLog.Session(directory, TextWriter.Null, TextWriter.Null))
        {
            session.Output.WriteLine(new string('x', 20_000) + "sensitive-tail");
            path = session.CurrentPath;
        }
        var content = File.ReadAllText(path);
        Assert.Contains("Oversized log line omitted", content);
        Assert.DoesNotContain("sensitive-tail", content);
        Assert.True(content.Length < 200);
    }

    [Fact]
    public void Session_SerializesConcurrentOutputAndErrors()
    {
        var directory = Path.Combine(TestEnvironment.Directory, "log-parallel-" + Guid.NewGuid().ToString("N"));
        string path;
        using (var session = new CompanionLog.Session(directory, TextWriter.Null, TextWriter.Null))
        {
            Parallel.For(0, 100, i => { session.Output.WriteLine("message " + i); session.Error.WriteLine("error " + i); });
            path = session.CurrentPath;
        }
        Assert.Equal(200, File.ReadAllLines(path).Length);
    }
}
