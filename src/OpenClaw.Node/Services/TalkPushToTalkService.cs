using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Node.Protocol;

namespace OpenClaw.Node.Services
{
    internal sealed class TalkPushToTalkService : IDisposable
    {
        private sealed class Capture : IDisposable
        {
            public required string Id { get; init; }
            public required Process Process { get; init; }
            public required Task<string> StdOut { get; init; }
            public required Task<string> StdErr { get; init; }
            public void Dispose() => Process.Dispose();
        }

        private readonly object _gate = new();
        private readonly IGatewayRequestClient? _operator;
        private readonly string _sessionKey;
        private Capture? _active;

        public TalkPushToTalkService(IGatewayRequestClient? operatorClient, string sessionKey)
        {
            _operator = operatorClient;
            _sessionKey = sessionKey;
        }

        public Task<object> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_active != null) return Task.FromResult<object>(new { captureId = _active.Id });
                var id = Guid.NewGuid().ToString();
                _active = StartCapture(id);
                return Task.FromResult<object>(new { captureId = id });
            }
        }

        public async Task<object> StopAsync(CancellationToken cancellationToken)
        {
            Capture? capture;
            lock (_gate)
            {
                capture = _active;
                _active = null;
            }
            if (capture == null) return new { captureId = Guid.NewGuid().ToString(), transcript = (string?)null, status = "idle" };
            return await FinishCaptureAsync(capture, cancellationToken);
        }

        private async Task<object> FinishCaptureAsync(Capture capture, CancellationToken cancellationToken)
        {
            using (capture)
            {
                try
                {
                    await capture.Process.StandardInput.WriteLineAsync(string.Empty);
                    await capture.Process.StandardInput.FlushAsync();
                    using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stopCts.CancelAfter(TimeSpan.FromSeconds(6));
                    await capture.Process.WaitForExitAsync(stopCts.Token);
                    var output = await capture.StdOut;
                    var error = await capture.StdErr;
                    if (capture.Process.ExitCode != 0)
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "MIC_PERMISSION_REQUIRED: Windows speech recognition is unavailable" : error.Trim());
                    var transcript = ExtractTranscript(output);
                    if (string.IsNullOrWhiteSpace(transcript)) return new { captureId = capture.Id, transcript = (string?)null, status = "empty" };
                    var queued = await QueueTranscriptAsync(capture.Id, transcript, cancellationToken);
                    return new { captureId = capture.Id, transcript, status = queued ? "queued" : "offline" };
                }
                catch
                {
                    try { if (!capture.Process.HasExited) capture.Process.Kill(entireProcessTree: true); } catch { }
                    throw;
                }
            }
        }

        public Task<object> CancelAsync()
        {
            Capture? capture;
            lock (_gate)
            {
                capture = _active;
                _active = null;
            }
            if (capture == null) return Task.FromResult<object>(new { captureId = Guid.NewGuid().ToString(), transcript = (string?)null, status = "idle" });
            using (capture)
            {
                try { capture.Process.Kill(entireProcessTree: true); } catch { }
                return Task.FromResult<object>(new { captureId = capture.Id, transcript = (string?)null, status = "cancelled" });
            }
        }

        public async Task<object> OnceAsync(CancellationToken cancellationToken)
        {
            Capture capture;
            lock (_gate)
            {
                if (_active != null)
                    return new { captureId = _active.Id, transcript = (string?)null, status = "busy" };
                var id = Guid.NewGuid().ToString();
                capture = StartCapture(id);
                _active = capture;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
                return await StopCaptureAsync(capture.Id, cancellationToken);
            }
            catch
            {
                CancelCapture(capture.Id);
                throw;
            }
        }

        private async Task<object> StopCaptureAsync(string expectedId, CancellationToken cancellationToken)
        {
            Capture? capture;
            lock (_gate)
            {
                capture = _active?.Id == expectedId ? _active : null;
                if (capture != null) _active = null;
            }
            if (capture == null) return new { captureId = expectedId, transcript = (string?)null, status = "cancelled" };
            return await FinishCaptureAsync(capture, cancellationToken);
        }

        private void CancelCapture(string expectedId)
        {
            Capture? capture;
            lock (_gate)
            {
                capture = _active?.Id == expectedId ? _active : null;
                if (capture != null) _active = null;
            }
            if (capture == null) return;
            using (capture)
            {
                try { capture.Process.Kill(entireProcessTree: true); } catch { }
            }
        }

        private Capture StartCapture(string id)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("MIC_PERMISSION_REQUIRED: push-to-talk requires Windows");
            const string script = @"
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Speech
$recognizer=New-Object System.Speech.Recognition.SpeechRecognitionEngine([System.Globalization.CultureInfo]::CurrentCulture)
$recognizer.LoadGrammar((New-Object System.Speech.Recognition.DictationGrammar))
$recognizer.SetInputToDefaultAudioDevice()
$parts=New-Object System.Collections.Generic.List[string]
$recognizer.add_SpeechRecognized({ param($sender,$eventArgs) if($eventArgs.Result.Confidence -ge 0.20){ $parts.Add($eventArgs.Result.Text) } })
$recognizer.RecognizeAsync([System.Speech.Recognition.RecognizeMode]::Multiple)
[Console]::In.ReadLine() | Out-Null
$recognizer.RecognizeAsyncStop()
Start-Sleep -Milliseconds 500
$recognizer.Dispose()
@{ transcript=($parts -join ' ').Trim() } | ConvertTo-Json -Compress
";
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            ChildProcessSecurity.ScrubSensitiveEnvironment(start);
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Sta");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(script);
            var process = new Process { StartInfo = start };
            process.Start();
            return new Capture
            {
                Id = id,
                Process = process,
                StdOut = process.StandardOutput.ReadToEndAsync(),
                StdErr = process.StandardError.ReadToEndAsync(),
            };
        }

        private async Task<bool> QueueTranscriptAsync(string captureId, string transcript, CancellationToken cancellationToken)
        {
            if (_operator?.IsConnected != true) return false;
            try
            {
                await _operator.RequestAsync<JsonElement>("chat.send", new
                {
                    sessionKey = _sessionKey,
                    message = transcript,
                    deliver = false,
                    suppressCommandInterpretation = false,
                    idempotencyKey = "windows-ptt-" + captureId,
                }, cancellationToken, 30_000);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? ExtractTranscript(string output)
        {
            var last = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(last)) return null;
            using var document = JsonDocument.Parse(last);
            return document.RootElement.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.String
                ? transcript.GetString()?.Trim()
                : null;
        }

        public void Dispose() => CancelAsync().GetAwaiter().GetResult();
    }
}
