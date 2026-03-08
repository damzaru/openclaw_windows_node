using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;

namespace OpenClaw.Node.Services
{
    public class CameraCaptureService
    {
        public sealed class CameraDeviceInfo
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Position { get; set; } = "front";
            public string DeviceType { get; set; } = "unknown";
        }

        private const string PlaceholderJpegBase64 = @"/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a
HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIy
MjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB
/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAA
AAAAAAAAAAH/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCdAABP/9k=";

        public string? LastError { get; private set; }

        public async Task<CameraDeviceInfo[]> ListDevicesAsync()
        {
            LastError = null;
            if (!OperatingSystem.IsWindows())
            {
                LastError = "camera not supported on non-Windows host";
                return Array.Empty<CameraDeviceInfo>();
            }

            var (devices, error) = await TryListDevicesWithWinRtAsync();
            if (devices.Length > 0)
            {
                LastError = null;
                return devices;
            }

            var ffmpeg = await ResolveFfmpegPathAsync();
            if (!string.IsNullOrWhiteSpace(ffmpeg))
            {
                var (fallbackDevices, ffErr) = await TryListDevicesWithFfmpegAsync(ffmpeg);
                if (fallbackDevices.Length > 0)
                {
                    LastError = null;
                    return fallbackDevices;
                }

                LastError = string.Join(" | ", new[] { error, ffErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return Array.Empty<CameraDeviceInfo>();
            }

            LastError = error;
            return Array.Empty<CameraDeviceInfo>();
        }

        public async Task<(string Base64, int Width, int Height)> CaptureJpegAsBase64Async(
            string facing,
            int? maxWidth,
            double? quality,
            int? delayMs,
            string? deviceId)
        {
            LastError = null;
            var clampedDelay = Math.Clamp(delayMs ?? 0, 0, 10000);
            if (clampedDelay > 0) await Task.Delay(clampedDelay);

            if (!OperatingSystem.IsWindows())
            {
                LastError = "camera not supported on non-Windows host";
                return (PlaceholderJpegBase64, 1, 1);
            }

            var resolvedWinRtDeviceId = await ResolvePreferredWinRtDeviceIdAsync(facing, deviceId);
            var (bytes, error) = await TryCaptureWithWinRtAsync(resolvedWinRtDeviceId);
            if (bytes != null && bytes.Length > 0 && IsLikelyJpeg(bytes))
            {
                var effective = ApplyJpegOutputOptions(bytes, maxWidth, quality);
                LastError = null;
                var (w, h) = TryReadJpegDimensions(effective);
                return (Convert.ToBase64String(effective), Math.Max(w, 1), Math.Max(h, 1));
            }

            // Optional shipped fallback: bundled ffmpeg binary (no user install required).
            var ffmpeg = await ResolveFfmpegPathAsync();
            if (!string.IsNullOrWhiteSpace(ffmpeg))
            {
                var (ffBytes, ffErr) = await TryCaptureWithFfmpegAsync(ffmpeg, facing, maxWidth, quality, deviceId);
                if (ffBytes != null && ffBytes.Length > 0 && IsLikelyJpeg(ffBytes))
                {
                    LastError = null;
                    var (fw, fh) = TryReadJpegDimensions(ffBytes);
                    return (Convert.ToBase64String(ffBytes), Math.Max(fw, 1), Math.Max(fh, 1));
                }

                LastError = string.Join(" | ", new[] { error, ffErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return (PlaceholderJpegBase64, 1, 1);
            }

            LastError = error;
            return (PlaceholderJpegBase64, 1, 1);
        }

        private async Task<string?> ResolvePreferredWinRtDeviceIdAsync(string facing, string? explicitDeviceId)
        {
            if (!string.IsNullOrWhiteSpace(explicitDeviceId))
            {
                return explicitDeviceId;
            }

            var (devices, _) = await TryListDevicesWithWinRtAsync();
            if (devices.Length == 0)
            {
                return null;
            }

            var desiredFacing = string.Equals(facing, "back", StringComparison.OrdinalIgnoreCase) ? "back" : "front";
            var selected = devices.FirstOrDefault(d =>
                string.Equals(d.Position, desiredFacing, StringComparison.OrdinalIgnoreCase)) ?? devices[0];
            return selected.Id;
        }

        private static byte[] ApplyJpegOutputOptions(byte[] jpegBytes, int? maxWidth, double? quality)
        {
            if (!maxWidth.HasValue && !quality.HasValue)
            {
                return jpegBytes;
            }

            try
            {
                var (srcW, _) = TryReadJpegDimensions(jpegBytes);
                var targetWidth = Math.Clamp(maxWidth ?? Math.Max(srcW, 1), 64, 8000);
                var targetQuality = Math.Clamp(quality ?? 0.92, 0.1, 1.0);
                var encoded = ImageEncoding.EncodeJpegBase64(jpegBytes, targetWidth, targetQuality);
                if (string.IsNullOrWhiteSpace(encoded.Base64))
                {
                    return jpegBytes;
                }

                return Convert.FromBase64String(encoded.Base64);
            }
            catch
            {
                return jpegBytes;
            }
        }

        private static async Task<(CameraDeviceInfo[] Devices, string? Error)> TryListDevicesWithWinRtAsync()
        {
            var script = @"
$ErrorActionPreference='Stop'
try {
  $op=[Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType=WindowsRuntime]::FindAllAsync([Windows.Devices.Enumeration.DeviceClass]::VideoCapture)
  while($op.Status -eq [Windows.Foundation.AsyncStatus]::Started){ Start-Sleep -Milliseconds 25 }
  if($op.Status -ne [Windows.Foundation.AsyncStatus]::Completed){ throw ('FindAllAsync failed: ' + $op.Status) }
  $devices=$op.GetResults()
  $arr=@()
  foreach($d in $devices){
    $arr += [pscustomobject]@{ id=$d.Id; name=$d.Name }
  }
  $arr | ConvertTo-Json -Compress
} catch {
  Write-Output ('__OC_ERR__' + $_.Exception.Message)
  exit 1
}
";

            var res = await RunPowerShellAsync(script, null);
            var output = (res.StdOut ?? string.Empty).Trim();
            var stderr = (res.StdErr ?? string.Empty).Trim();

            if (res.ExitCode != 0)
            {
                var err = ExtractMarkedError(output) ?? stderr;
                if (string.IsNullOrWhiteSpace(err)) err = "WinRT device enumeration failed";
                return (Array.Empty<CameraDeviceInfo>(), err);
            }

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "[]" : output);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return (Array.Empty<CameraDeviceInfo>(), "WinRT device enumeration returned invalid payload");
                }

                var list = new List<CameraDeviceInfo>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? (idEl.GetString() ?? string.Empty)
                        : string.Empty;
                    var name = item.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                        ? (nEl.GetString() ?? string.Empty)
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) continue;

                    var lower = name.ToLowerInvariant();
                    var position = (lower.Contains("back") || lower.Contains("rear")) ? "back" : "front";
                    var deviceType = (lower.Contains("usb") || lower.Contains("external")) ? "external" : "integrated";

                    list.Add(new CameraDeviceInfo
                    {
                        Id = string.IsNullOrWhiteSpace(id) ? name : id,
                        Name = string.IsNullOrWhiteSpace(name) ? id : name,
                        Position = position,
                        DeviceType = deviceType,
                    });
                }

                return (list.ToArray(), null);
            }
            catch (Exception ex)
            {
                return (Array.Empty<CameraDeviceInfo>(), $"WinRT device parse failed: {ex.Message}");
            }
        }

        private static async Task<(byte[]? Bytes, string? Error)> TryCaptureWithWinRtAsync(string? deviceId)
        {
            var env = new Dictionary<string, string?> { ["OPENCLAW_CAMERA_DEVICEID"] = deviceId ?? string.Empty };
            var script = @"
$ErrorActionPreference='Stop'
try {
  $deviceId=$env:OPENCLAW_CAMERA_DEVICEID
  $settings=[Windows.Media.Capture.MediaCaptureInitializationSettings, Windows.Media.Capture, ContentType=WindowsRuntime]::new()
  $settings.StreamingCaptureMode=[Windows.Media.Capture.StreamingCaptureMode]::Video
  if($deviceId){ $settings.VideoDeviceId=$deviceId }

  $mc=[Windows.Media.Capture.MediaCapture, Windows.Media.Capture, ContentType=WindowsRuntime]::new()
  $init=$mc.InitializeAsync($settings)
  while($init.Status -eq [Windows.Foundation.AsyncStatus]::Started){ Start-Sleep -Milliseconds 25 }
  if($init.Status -ne [Windows.Foundation.AsyncStatus]::Completed){ throw ('InitializeAsync failed: ' + $init.Status) }
  $null=$init.GetResults()

  $jpeg=[Windows.Media.MediaProperties.ImageEncodingProperties, Windows.Media.MediaProperties, ContentType=WindowsRuntime]::CreateJpeg()
  $stream=[Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]::new()
  $cap=$mc.CapturePhotoToStreamAsync($jpeg,$stream)
  while($cap.Status -eq [Windows.Foundation.AsyncStatus]::Started){ Start-Sleep -Milliseconds 25 }
  if($cap.Status -ne [Windows.Foundation.AsyncStatus]::Completed){ throw ('CapturePhotoToStreamAsync failed: ' + $cap.Status) }
  $null=$cap.GetResults()

  $stream.Seek(0)
  $size=[int]$stream.Size
  if($size -le 0){ throw 'Empty stream' }

  $reader=[Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType=WindowsRuntime]::new($stream.GetInputStreamAt(0))
  $load=$reader.LoadAsync($size)
  while($load.Status -eq [Windows.Foundation.AsyncStatus]::Started){ Start-Sleep -Milliseconds 25 }
  if($load.Status -ne [Windows.Foundation.AsyncStatus]::Completed){ throw ('LoadAsync failed: ' + $load.Status) }
  $null=$load.GetResults()

  $bytes=New-Object byte[] $size
  $reader.ReadBytes($bytes)
  [Convert]::ToBase64String($bytes)
} catch {
  Write-Output ('__OC_ERR__' + $_.Exception.Message)
  exit 1
}
";

            var res = await RunPowerShellAsync(script, env);
            var output = (res.StdOut ?? string.Empty).Trim();
            var stderr = (res.StdErr ?? string.Empty).Trim();

            if (res.ExitCode != 0)
            {
                var err = ExtractMarkedError(output) ?? stderr;
                if (string.IsNullOrWhiteSpace(err)) err = "WinRT camera capture failed";
                return (null, err);
            }

            try
            {
                var raw = Convert.FromBase64String(output);
                if (!IsLikelyJpeg(raw))
                {
                    return (null, "WinRT capture returned non-JPEG payload");
                }

                return (raw, null);
            }
            catch (Exception ex)
            {
                return (null, $"WinRT camera decode failed: {ex.Message}");
            }
        }

        private static async Task<string?> ResolveFfmpegPathAsync()
        {
            // 1) explicit override
            var env = Environment.GetEnvironmentVariable("OPENCLAW_FFMPEG_PATH");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

            // 2) bundled locations (what we ship)
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg.exe"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "tools", "ffmpeg", "ffmpeg.exe")),
            };

            foreach (var c in candidates)
            {
                try
                {
                    if (File.Exists(c)) return c;
                }
                catch { }
            }

            // 3) runtime resolution for dev environments (Get-Command often works even when `where` doesn't)
            var cmd = await TryResolveFfmpegViaGetCommandAsync();
            if (!string.IsNullOrWhiteSpace(cmd) && File.Exists(cmd))
            {
                return cmd;
            }

            return null;
        }

        private static async Task<string?> TryResolveFfmpegViaGetCommandAsync()
        {
            var script = @"
$ErrorActionPreference='Stop'
try {
  $c = Get-Command ffmpeg -ErrorAction Stop
  if($c -and $c.Path){ $c.Path; exit 0 }
  exit 1
} catch {
  exit 1
}
";

            var res = await RunPowerShellAsync(script, null);
            if (res.ExitCode != 0) return null;
            var path = (res.StdOut ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        private static async Task<(CameraDeviceInfo[] Devices, string? Error)> TryListDevicesWithFfmpegAsync(string ffmpegPath)
        {
            var res = await RunProcessAsync(ffmpegPath, "-hide_banner", "-f", "dshow", "-list_devices", "true", "-i", "dummy");
            var text = (res.StdErr ?? string.Empty) + "\n" + (res.StdOut ?? string.Empty);
            var names = ParseDshowVideoDeviceNames(text);
            if (names.Length == 0)
            {
                var err = string.IsNullOrWhiteSpace(res.StdErr) ? "ffmpeg device list returned no video devices" : res.StdErr.Trim();
                return (Array.Empty<CameraDeviceInfo>(), err);
            }

            var list = names.Select(name =>
            {
                var lower = name.ToLowerInvariant();
                var position = (lower.Contains("back") || lower.Contains("rear")) ? "back" : "front";
                var deviceType = (lower.Contains("usb") || lower.Contains("external")) ? "external" : "integrated";
                return new CameraDeviceInfo
                {
                    Id = name,
                    Name = name,
                    Position = position,
                    DeviceType = deviceType,
                };
            }).ToArray();

            return (list, null);
        }

        private static async Task<(byte[]? Bytes, string? Error)> TryCaptureWithFfmpegAsync(
            string ffmpegPath,
            string facing,
            int? maxWidth,
            double? quality,
            string? deviceId)
        {
            var (devices, listErr) = await TryListDevicesWithFfmpegAsync(ffmpegPath);
            if (devices.Length == 0)
            {
                return (null, listErr ?? "ffmpeg camera device enumeration failed");
            }

            CameraDeviceInfo? selected = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                selected = devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            }

            if (selected == null)
            {
                var desiredFacing = string.Equals(facing, "back", StringComparison.OrdinalIgnoreCase) ? "back" : "front";
                selected = devices.FirstOrDefault(d => string.Equals(d.Position, desiredFacing, StringComparison.OrdinalIgnoreCase)) ?? devices[0];
            }

            var q = quality ?? 0.85;
            q = Math.Clamp(q, 0.0, 1.0);
            var qv = (int)Math.Round(31 - (q * 29));

            var tempFile = Path.Combine(Path.GetTempPath(), $"openclaw_cam_ff_{Guid.NewGuid():N}.jpg");
            try
            {
                var args = new List<string>
                {
                    "-hide_banner",
                    "-loglevel", "error",
                    "-y",
                    "-f", "dshow",
                    "-i", $"video={selected.Name}",
                    "-frames:v", "1",
                    "-q:v", qv.ToString()
                };

                if (maxWidth.HasValue && maxWidth.Value > 0)
                {
                    args.Add("-vf");
                    args.Add($"scale='min({maxWidth.Value},iw)':-2");
                }

                args.Add(tempFile);
                var res = await RunProcessAsync(ffmpegPath, args.ToArray());
                if (res.ExitCode != 0 || !File.Exists(tempFile))
                {
                    var err = string.IsNullOrWhiteSpace(res.StdErr) ? "ffmpeg capture command failed" : res.StdErr.Trim();
                    return (null, err);
                }

                var bytes = await File.ReadAllBytesAsync(tempFile);
                if (!IsLikelyJpeg(bytes))
                {
                    return (null, "ffmpeg capture produced non-JPEG output");
                }

                return (bytes, null);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        private static string[] ParseDshowVideoDeviceNames(string output)
        {
            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var names = new List<string>();

            // Pattern used by ffmpeg dshow device listing lines:
            // "USB2.0 FHD UVC WebCam" (video)
            var typedQuoted = new Regex("\"([^\"]+)\"\\s*\\((video|audio)\\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            foreach (var line in lines)
            {
                var mt = typedQuoted.Match(line);
                if (mt.Success)
                {
                    var kind = mt.Groups[2].Value;
                    if (!string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var typedName = mt.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(typedName) && !names.Contains(typedName, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(typedName);
                    }
                }
            }

            if (names.Count > 0)
            {
                return names.ToArray();
            }

            // Fallback to section-based parser for other ffmpeg variants.
            var inVideo = false;
            var quoted = new Regex("\"([^\"]+)\"", RegexOptions.Compiled);

            foreach (var line in lines)
            {
                if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
                {
                    inVideo = true;
                    continue;
                }

                if (inVideo && line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (!inVideo) continue;

                var m = quoted.Match(line);
                if (!m.Success) continue;
                var name = m.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.StartsWith("@device_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }

            return names.ToArray();
        }

        private static string? ExtractMarkedError(string output)
        {
            const string mark = "__OC_ERR__";
            var idx = output.IndexOf(mark, StringComparison.Ordinal);
            if (idx < 0) return null;
            return output[(idx + mark.Length)..].Trim();
        }

        private static bool IsLikelyJpeg(byte[] bytes)
        {
            return bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8;
        }

        private static (int Width, int Height) TryReadJpegDimensions(byte[] bytes)
        {
            if (!IsLikelyJpeg(bytes)) return (0, 0);

            var i = 2;
            while (i + 9 < bytes.Length)
            {
                if (bytes[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                var marker = bytes[i + 1];
                i += 2;
                if (marker == 0xD9 || marker == 0xDA) break;
                if (i + 1 >= bytes.Length) break;

                var len = (bytes[i] << 8) + bytes[i + 1];
                if (len < 2 || i + len > bytes.Length) break;

                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                {
                    if (i + 7 >= bytes.Length) break;
                    var h = (bytes[i + 3] << 8) + bytes[i + 4];
                    var w = (bytes[i + 5] << 8) + bytes[i + 6];
                    return (w, h);
                }

                i += len;
            }

            return (0, 0);
        }

        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var p = new Process { StartInfo = psi };
            p.Start();
            var soTask = p.StandardOutput.ReadToEndAsync();
            var seTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var so = await soTask;
            var se = await seTask;
            return (p.ExitCode, so, se);
        }

        private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPowerShellAsync(string script, IDictionary<string, string?>? env)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Sta");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            if (env != null)
            {
                foreach (var kv in env)
                {
                    psi.Environment[kv.Key] = kv.Value ?? string.Empty;
                }
            }

            using var p = new Process { StartInfo = psi };
            p.Start();
            var soTask = p.StandardOutput.ReadToEndAsync();
            var seTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var so = await soTask;
            var se = await seTask;
            return (p.ExitCode, so, se);
        }
    }
}
