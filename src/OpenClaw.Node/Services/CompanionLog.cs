using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Node.Services;

/// <summary>Bounded, credential-redacted logs for console and double-clicked tray launches.</summary>
internal static class CompanionLog
{
    private static readonly object SecretGate = new();
    private static readonly HashSet<string> Secrets = new(StringComparer.Ordinal);
    internal static string DirectoryPath => Path.Combine(new SecureStore().BaseDirectory, "logs");
    internal static void RegisterSecret(string? value)
    {
        if (!string.IsNullOrEmpty(value)) lock (SecretGate) Secrets.Add(value);
    }
    internal static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?:https?|wss?)://[^\s""<>]+", match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[redacted-url]";
            var builder = new UriBuilder(uri) { UserName = "", Password = "", Fragment = "" };
            if (!string.IsNullOrEmpty(uri.Query)) builder.Query = "redacted";
            return builder.Uri.AbsoluteUri;
        }, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(/__openclaw__/cap/)[^/?#\s]+", "$1[redacted]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text,
            @"(?<key>[""']?\b(?:token|gatewayToken|deviceToken|bootstrapToken|password|vncPassword|authToken|ipcCredential|privateKey|signature|authorization)\b[""']?\s*[:=]\s*)(?:(?:Bearer|Basic)\s+[^\s,;}]+|""(?:\\.|[^""])*""|'[^']*'|[^\s,;}]+)",
            "${key}[redacted]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bBearer\s+[^\s,;}]+", "Bearer [redacted]", RegexOptions.IgnoreCase);
        lock (SecretGate)
            foreach (var secret in Secrets.OrderByDescending(value => value.Length)) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }
    internal static IDisposable Start()
    {
        try { return new Session(DirectoryPath, Console.Out, Console.Error, installConsole: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine("[LOG] Could not initialize persistent logs: " + Redact(ex.Message));
            return new EmptySession();
        }
    }
    private sealed class EmptySession : IDisposable { public void Dispose() { } }

    internal sealed class Session : IDisposable
    {
        private readonly TextWriter _originalOut, _originalError;
        private readonly TextWriter? _installedOut, _installedError;
        private readonly Sink _sink;
        internal TextWriter Output { get; }
        internal TextWriter Error { get; }
        internal string CurrentPath => _sink.CurrentPath;
        internal Session(string directory, TextWriter output, TextWriter error, bool installConsole = false, int maximumBytes = 4 * 1024 * 1024)
        {
            _originalOut = output; _originalError = error;
            _sink = new Sink(directory, error, maximumBytes);
            Output = TextWriter.Synchronized(new LogWriter(_sink, output, "INFO"));
            Error = TextWriter.Synchronized(new LogWriter(_sink, error, "ERROR"));
            if (installConsole)
            {
                Console.SetOut(Output); Console.SetError(Error);
                _installedOut = Console.Out; _installedError = Console.Error;
            }
        }
        public void Dispose()
        {
            Output.Flush(); Error.Flush();
            if (_installedOut != null && ReferenceEquals(Console.Out, _installedOut)) Console.SetOut(_originalOut);
            if (_installedError != null && ReferenceEquals(Console.Error, _installedError)) Console.SetError(_originalError);
            _sink.Dispose();
        }
    }

    private sealed class LogWriter(Sink sink, TextWriter original, string level) : TextWriter
    {
        private readonly StringBuilder _line = new();
        private bool _oversized;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            if (value == '\n') Emit();
            else if (value != '\r' && !_oversized)
            {
                _line.Append(value);
                if (_line.Length >= 16 * 1024) { _line.Clear(); _oversized = true; }
            }
        }
        public override void Write(string? value) { if (value != null) foreach (var character in value) Write(character); }
        public override void Write(char[] buffer, int index, int count) { for (var i = index; i < index + count; i++) Write(buffer[i]); }
        private void Emit()
        {
            var safe = _oversized ? "[LOG] Oversized log line omitted" : Redact(_line.ToString());
            _line.Clear(); _oversized = false;
            try { original.WriteLine(safe); } catch (IOException) { } catch (ObjectDisposedException) { }
            sink.Write(level, safe);
        }
        public override void Flush() { if (_line.Length > 0 || _oversized) Emit(); sink.Flush(); }
    }

    private sealed class Sink : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _directory, _prefix;
        private readonly TextWriter _fallback;
        private readonly int _maximumBytes;
        private StreamWriter? _writer;
        private int _part;
        internal string CurrentPath { get; private set; } = "";
        internal Sink(string directory, TextWriter fallback, int maximumBytes)
        {
            _directory = Path.GetFullPath(directory); _fallback = fallback; _maximumBytes = Math.Max(128, maximumBytes);
            Directory.CreateDirectory(_directory);
            _prefix = $"companion-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Guid.NewGuid():N}";
            Open();
        }
        private void Open()
        {
            CurrentPath = Path.Combine(_directory, $"{_prefix}-{_part++}.log");
            _writer = new StreamWriter(new FileStream(CurrentPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            Cleanup();
        }
        private void Cleanup()
        {
            try
            {
                var files = new DirectoryInfo(_directory).GetFiles("companion-*.log").OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
                foreach (var file in files.Skip(10))
                {
                    if (file.FullName == CurrentPath || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private void Fail(Exception ex)
        {
            try { _writer?.Dispose(); } catch (IOException) { }
            _writer = null;
            try { _fallback.WriteLine("[LOG] Persistent logging stopped: " + Redact(ex.Message)); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
        internal void Write(string level, string line)
        {
            lock (_gate)
            {
                if (_writer == null) return;
                try
                {
                    if (_writer.BaseStream.Position >= _maximumBytes) { _writer.Dispose(); Open(); }
                    _writer.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] {line}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(ex); }
            }
        }
        internal void Flush()
        {
            lock (_gate) try { _writer?.Flush(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(ex); }
        }
        public void Dispose()
        {
            lock (_gate) { try { _writer?.Dispose(); } catch (IOException) { } catch (UnauthorizedAccessException) { } _writer = null; }
        }
    }
}
