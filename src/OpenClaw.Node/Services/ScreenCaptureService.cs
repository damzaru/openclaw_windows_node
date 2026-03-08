using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Reflection;

#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using Forms = System.Windows.Forms;
using ScreenRecorderLib;
#endif

namespace OpenClaw.Node.Services
{
    public class ScreenCaptureService : IScreenImageProvider
    {
        public sealed class ScreenDisplayInfo
        {
            public int Index { get; set; }
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public bool Primary { get; set; }
        }

        public sealed class ScreenRecordResult
        {
            public string Base64 { get; set; } = string.Empty;
            public string CaptureApi { get; set; } = "auto";
            public bool HardwareEncoding { get; set; }
            public bool LowLatency { get; set; }
        }

        public const string Png1x1FallbackBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        public Task<ScreenDisplayInfo[]> ListDisplaysAsync()
        {
#if WINDOWS
            if (!OperatingSystem.IsWindows()) return Task.FromResult(Array.Empty<ScreenDisplayInfo>());

            try
            {
                var screens = Forms.Screen.AllScreens;
                var result = new ScreenDisplayInfo[screens.Length];
                for (var i = 0; i < screens.Length; i++)
                {
                    var s = screens[i];
                    result[i] = new ScreenDisplayInfo
                    {
                        Index = i,
                        Id = s.DeviceName,
                        Name = s.DeviceName, 
                        X = s.Bounds.X,
                        Y = s.Bounds.Y,
                        Width = s.Bounds.Width,
                        Height = s.Bounds.Height,
                        Primary = s.Primary
                    };
                }
                return Task.FromResult(result);
            }
            catch
            {
                return Task.FromResult(Array.Empty<ScreenDisplayInfo>());
            }
#else
            return Task.FromResult(Array.Empty<ScreenDisplayInfo>());
#endif
        }

        public async Task<string> CaptureScreenshotAsync(int screenIndex = 0, string format = "png")
        {
#if WINDOWS
            if (!OperatingSystem.IsWindows()) return await TakeScreenshotFallbackAsync();

            return await Task.Run(() =>
            {
                try
                {
                    var screens = Forms.Screen.AllScreens;
                    if (screens.Length == 0) return string.Empty;
                    
                    var safeIndex = Math.Clamp(screenIndex, 0, screens.Length - 1);
                    var screen = screens[safeIndex];

                    using var bitmap = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                    }

                    using var ms = new MemoryStream();
                    var fmt = GetImageFormat(format);
                    bitmap.Save(ms, fmt);
                    return Convert.ToBase64String(ms.ToArray());
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Screenshot failed", ex);
                }
            });
#else
            return await TakeScreenshotFallbackAsync();
#endif
        }

        public async Task<(byte[] bytes, int width, int height)> CaptureScreenshotBytesAsync(int screenIndex = 0, string format = "png")
        {
#if WINDOWS
            if (!OperatingSystem.IsWindows()) return await TakeScreenshotFallbackBytesAsync();

            return await Task.Run(() =>
            {
                var screens = Forms.Screen.AllScreens;
                if (screens.Length == 0) return (Array.Empty<byte>(), 0, 0);

                var safeIndex = Math.Clamp(screenIndex, 0, screens.Length - 1);
                var screen = screens[safeIndex];

                using var bitmap = new Bitmap(screen.Bounds.Width, screen.Bounds.Height);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                }

                using var ms = new MemoryStream();
                var fmt = GetImageFormat(format);
                bitmap.Save(ms, fmt);
                var bytes = ms.ToArray();
                return (bytes, bitmap.Width, bitmap.Height);
            });
#else
            return await TakeScreenshotFallbackBytesAsync();
#endif
        }

        public async Task<(byte[] bytes, int width, int height)> CaptureWindowBytesAsync(long handle, string format = "png")
        {
#if WINDOWS
            if (!OperatingSystem.IsWindows()) return await TakeScreenshotFallbackBytesAsync();
            if (handle == 0) return (Array.Empty<byte>(), 0, 0);

            return await Task.Run(() =>
            {
                var hWnd = new IntPtr(handle);
                if (!GetWindowRect(hWnd, out var rect)) return (Array.Empty<byte>(), 0, 0);

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;

                if (width <= 0 || height <= 0) return (Array.Empty<byte>(), 0, 0);

                using var bitmap = new Bitmap(width, height);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                }

                using var ms = new MemoryStream();
                var fmt = GetImageFormat(format);
                bitmap.Save(ms, fmt);
                var bytes = ms.ToArray();
                return (bytes, bitmap.Width, bitmap.Height);
            });
#else
            return await TakeScreenshotFallbackBytesAsync();
#endif
        }

        public async Task<string> CaptureWindowAsync(long handle, string format = "png")
        {
#if WINDOWS
            if (!OperatingSystem.IsWindows()) return await TakeScreenshotFallbackAsync();
            if (handle == 0) return string.Empty;

            return await Task.Run(() =>
            {
                var hWnd = new IntPtr(handle);
                if (!GetWindowRect(hWnd, out var rect)) return string.Empty;

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;

                if (width <= 0 || height <= 0) return string.Empty;

                try 
                {
                    using var bitmap = new Bitmap(width, height);
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                    }

                    using var ms = new MemoryStream();
                    var fmt = GetImageFormat(format);
                    bitmap.Save(ms, fmt);
                    return Convert.ToBase64String(ms.ToArray());
                }
                catch
                {
                    return string.Empty;
                }
            });
#else
            return await TakeScreenshotFallbackAsync();
#endif
        }

        public async Task<ScreenRecordResult> RecordScreenAsBase64Async(
            int durationMs,
            int fps,
            bool includeAudio,
            int screenIndex = 0,
            string captureApi = "auto",
            bool lowLatency = false)
        {
#if WINDOWS
            if (!OperatingSystem.IsWindows())
            {
                return await CreateFallbackResultAsync();
            }

            durationMs = Math.Clamp(durationMs, 500, 120000);
            fps = Math.Clamp(fps, 1, 60);

            var attempts = new[]
            {
                new { Hardware = true, LowLatency = lowLatency, CaptureApi = captureApi },
                new { Hardware = false, LowLatency = lowLatency, CaptureApi = captureApi },
                new { Hardware = false, LowLatency = true, CaptureApi = "desktopduplication" }
            };

            Exception? lastError = null;
            foreach (var attempt in attempts)
            {
                try
                {
                    var b64 = await RecordSingleAttemptAsync(durationMs, fps, includeAudio, screenIndex, attempt.CaptureApi, attempt.Hardware, attempt.LowLatency);
                    return new ScreenRecordResult
                    {
                        Base64 = b64,
                        CaptureApi = string.IsNullOrWhiteSpace(attempt.CaptureApi) ? "auto" : attempt.CaptureApi,
                        HardwareEncoding = attempt.Hardware,
                        LowLatency = attempt.LowLatency,
                    };
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException($"Screen recording failed after fallback attempts: {lastError?.Message}", lastError);
#else
            return await CreateFallbackResultAsync();
#endif
        }

#if WINDOWS
        private static async Task<string> RecordSingleAttemptAsync(
            int durationMs,
            int fps,
            bool includeAudio,
            int screenIndex,
            string captureApi,
            bool hardwareEncoding,
            bool lowLatency)
        {
            var outputPath = Path.Combine(Path.GetTempPath(), $"openclaw_screen_record_{Guid.NewGuid():N}.mp4");
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var options = RecorderOptions.Default;
            options.VideoEncoderOptions.Framerate = fps;
            options.VideoEncoderOptions.IsFixedFramerate = true;
            options.VideoEncoderOptions.IsHardwareEncodingEnabled = hardwareEncoding;
            options.VideoEncoderOptions.IsMp4FastStartEnabled = true;
            options.VideoEncoderOptions.IsLowLatencyEnabled = lowLatency;
            options.VideoEncoderOptions.Quality = 70;

            options.AudioOptions.IsAudioEnabled = includeAudio;
            options.AudioOptions.IsOutputDeviceEnabled = includeAudio;
            options.AudioOptions.IsInputDeviceEnabled = false;

            var displays = Recorder.GetDisplays();
            if (displays != null && displays.Count > 0)
            {
                var safeIndex = Math.Clamp(screenIndex, 0, displays.Count - 1);
                options.SourceOptions = new SourceOptions();
                options.SourceOptions.RecordingSources.Add(new DisplayRecordingSource(displays[safeIndex].DeviceName));
            }
            else
            {
                options.SourceOptions = SourceOptions.MainMonitor;
            }

            TryApplyRecorderApi(options, captureApi);

            using var recorder = Recorder.CreateRecorder(options);

            recorder.OnRecordingComplete += (_, args) => completion.TrySetResult(args.FilePath);
            recorder.OnRecordingFailed += (_, args) => completion.TrySetException(new Exception(args.Error));
            recorder.OnStatusChanged += (_, args) =>
            {
                if (args.Status == RecorderStatus.Recording)
                {
                    started.TrySetResult();
                }
            };

            recorder.Record(outputPath);

            using (var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
            {
                try
                {
                    await started.Task.WaitAsync(startTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await Task.Delay(durationMs);
            recorder.Stop();

            string finalizedPath;
            using (var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                finalizedPath = await completion.Task.WaitAsync(completionTimeout.Token);
            }

            var fileToRead = string.IsNullOrWhiteSpace(finalizedPath) ? outputPath : finalizedPath;

            try
            {
                var bytes = await File.ReadAllBytesAsync(fileToRead);
                return Convert.ToBase64String(bytes);
            }
            finally
            {
                if (File.Exists(fileToRead))
                {
                    try { File.Delete(fileToRead); } catch { }
                }
            }
        }

        private static void TryApplyRecorderApi(RecorderOptions options, string captureApi)
        {
            if (string.IsNullOrWhiteSpace(captureApi) || string.Equals(captureApi, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var prop = options.GetType().GetProperty("RecorderApi");
                if (prop == null) return;

                var token = captureApi.Trim().ToLowerInvariant() switch
                {
                    "wgc" or "windowsgraphicscapture" => "WindowsGraphicsCapture",
                    "desktopduplication" or "desktop-duplication" or "duplication" or "dda" => "DesktopDuplication",
                    _ => captureApi.Trim(),
                };

                var enumValue = Enum.Parse(prop.PropertyType, token, ignoreCase: true);
                prop.SetValue(options, enumValue);
            }
            catch
            {
            }
        }
        
        private static System.Drawing.Imaging.ImageFormat GetImageFormat(string format)
        {
             return format.Trim().ToLowerInvariant() switch {
                 "jpg" or "jpeg" => System.Drawing.Imaging.ImageFormat.Jpeg,
                 "bmp" => System.Drawing.Imaging.ImageFormat.Bmp,
                 _ => System.Drawing.Imaging.ImageFormat.Png
             };
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
#endif

        private static Task<string> TakeScreenshotFallbackAsync()
        {
            return Task.FromResult(Png1x1FallbackBase64);
        }

        private static Task<(byte[] bytes, int width, int height)> TakeScreenshotFallbackBytesAsync()
        {
            try
            {
                var bytes = Convert.FromBase64String(Png1x1FallbackBase64);
                return Task.FromResult((bytes, 1, 1));
            }
            catch
            {
                return Task.FromResult((Array.Empty<byte>(), 0, 0));
            }
        }

        private static async Task<ScreenRecordResult> CreateFallbackResultAsync()
        {
            return new ScreenRecordResult
            {
                Base64 = await TakeScreenshotFallbackAsync(),
                CaptureApi = "fallback",
                HardwareEncoding = false,
                LowLatency = false,
            };
        }
    }
}
