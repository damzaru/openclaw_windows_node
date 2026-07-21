using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Node.Services
{
    internal sealed class WindowsDeviceService
    {
        private readonly object _locationGate = new();
        private string? _cachedLocationJson;
        private DateTimeOffset _cachedLocationTimestamp;

        public object GetInfo() => new
        {
            deviceName = Environment.MachineName,
            modelIdentifier = ResolveModelIdentifier(),
            systemName = "Windows",
            systemVersion = Environment.OSVersion.Version.ToString(),
            platform = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            appVersion = $"dev+{BuildInfo.BuildVersion}",
            appBuild = BuildInfo.BuildVersion,
            locale = CultureInfo.CurrentCulture.Name,
            timeZone = TimeZoneInfo.Local.Id,
        };

        public object GetStatus()
        {
            var systemDrive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            var total = systemDrive.IsReady ? systemDrive.TotalSize : 0;
            var free = systemDrive.IsReady ? systemDrive.AvailableFreeSpace : 0;
            var memory = ReadMemory();
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(value => value.OperationalStatus == OperationalStatus.Up)
                .Select(value => value.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "wifi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.GigabitEthernet => "wired",
                    NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "cellular",
                    _ => "other",
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new
            {
                battery = ReadBattery(),
                thermal = new { state = "nominal" },
                storage = new { totalBytes = total, freeBytes = free, usedBytes = Math.Max(0, total - free) },
                memory = new { totalBytes = memory.Total, availableBytes = memory.Available, usedBytes = Math.Max(0, memory.Total - memory.Available) },
                network = new
                {
                    status = NetworkInterface.GetIsNetworkAvailable() ? "satisfied" : "unsatisfied",
                    isExpensive = false,
                    isConstrained = false,
                    interfaces,
                },
                uptimeSeconds = Environment.TickCount64 / 1000d,
            };
        }

        public async Task<string> GetLocationJsonAsync(string? paramsJson, CancellationToken cancellationToken)
        {
            var timeoutMs = 10_000;
            int? maxAgeMs = null;
            var desiredAccuracy = "balanced";
            if (!string.IsNullOrWhiteSpace(paramsJson))
            {
                using var document = JsonDocument.Parse(paramsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("INVALID_REQUEST: location.get params must be an object");
                if (document.RootElement.TryGetProperty("timeoutMs", out var timeout))
                {
                    if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out var value) || value < 0)
                        throw new InvalidOperationException("INVALID_REQUEST: location.get timeoutMs must be a non-negative integer");
                    timeoutMs = Math.Clamp(value, 0, 60_000);
                }
                if (document.RootElement.TryGetProperty("maxAgeMs", out var maxAge))
                {
                    if (maxAge.ValueKind != JsonValueKind.Number || !maxAge.TryGetInt32(out var value) || value < 0)
                        throw new InvalidOperationException("INVALID_REQUEST: location.get maxAgeMs must be a non-negative integer");
                    maxAgeMs = value;
                }
                if (document.RootElement.TryGetProperty("desiredAccuracy", out var accuracy))
                {
                    if (accuracy.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("INVALID_REQUEST: location.get desiredAccuracy must be a string");
                    var requested = accuracy.GetString()?.Trim().ToLowerInvariant();
                    if (requested is not ("coarse" or "balanced" or "precise"))
                        throw new InvalidOperationException("INVALID_REQUEST: location.get desiredAccuracy must be coarse, balanced, or precise");
                    desiredAccuracy = requested;
                }
            }

            if (maxAgeMs.HasValue)
            {
                lock (_locationGate)
                {
                    if (_cachedLocationJson != null &&
                        (DateTimeOffset.UtcNow - _cachedLocationTimestamp).TotalMilliseconds <= maxAgeMs.Value)
                    {
                        return _cachedLocationJson;
                    }
                }
            }

            const string script = @"
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$positionType=[Windows.Devices.Geolocation.Geoposition,Windows.Devices.Geolocation,ContentType=WindowsRuntime]
$locator=New-Object Windows.Devices.Geolocation.Geolocator
$requestedAccuracy=$env:OPENCLAW_LOCATION_ACCURACY
$locator.DesiredAccuracy=if($requestedAccuracy -eq 'precise'){[Windows.Devices.Geolocation.PositionAccuracy]::High}else{[Windows.Devices.Geolocation.PositionAccuracy]::Default}
$operation=$locator.GetGeopositionAsync()
$method=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 } | Select-Object -First 1
$task=$method.MakeGenericMethod($positionType).Invoke($null,@($operation))
$task.Wait()
$coordinate=$task.Result.Coordinate
$point=$coordinate.Point.Position
$speed=if($null -ne $coordinate.Speed){[double]$coordinate.Speed}else{$null}
$heading=if($null -ne $coordinate.Heading){[double]$coordinate.Heading}else{$null}
[ordered]@{ lat=$point.Latitude; lon=$point.Longitude; accuracyMeters=$coordinate.Accuracy; altitudeMeters=$point.Altitude; speedMps=$speed; headingDeg=$heading; timestamp=$coordinate.Timestamp.ToString('o'); isPrecise=([double]$coordinate.Accuracy -le 100); source='windows-geolocation' } | ConvertTo-Json -Compress
";
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
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
            start.Environment["OPENCLAW_LOCATION_ACCURACY"] = desiredAccuracy;
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("LOCATION_TIMEOUT: no fix in time");
            }
            var output = (await stdout).Trim();
            var error = (await stderr).Trim();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("LOCATION_UNAVAILABLE: " + (string.IsNullOrWhiteSpace(error) ? "Windows location is disabled or unavailable" : error));
            using var parsed = JsonDocument.Parse(output);
            var json = parsed.RootElement.GetRawText();
            var timestamp = DateTimeOffset.UtcNow;
            if (parsed.RootElement.TryGetProperty("timestamp", out var timestampElement) &&
                timestampElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(timestampElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTimestamp))
            {
                timestamp = parsedTimestamp;
            }
            lock (_locationGate)
            {
                _cachedLocationJson = json;
                _cachedLocationTimestamp = timestamp;
            }
            return json;
        }

        private static object ReadBattery()
        {
            try
            {
                var systemInformation = Type.GetType("System.Windows.Forms.SystemInformation, System.Windows.Forms");
                var power = systemInformation?.GetProperty("PowerStatus")?.GetValue(null);
                if (power != null)
                {
                    var level = Convert.ToDouble(power.GetType().GetProperty("BatteryLifePercent")?.GetValue(power) ?? -1d, CultureInfo.InvariantCulture);
                    var status = power.GetType().GetProperty("PowerLineStatus")?.GetValue(power)?.ToString();
                    return new
                    {
                        level = level is >= 0 and <= 1 ? level : (double?)null,
                        state = string.Equals(status, "Online", StringComparison.OrdinalIgnoreCase) ? "charging" : "unplugged",
                        lowPowerModeEnabled = false,
                    };
                }
            }
            catch { }
            return new { level = (double?)null, state = "unknown", lowPowerModeEnabled = false };
        }

        private static (long Total, long Available) ReadMemory()
        {
            if (!OperatingSystem.IsWindows()) return (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 0);
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (0, 0);
        }

        private static string ResolveModelIdentifier()
        {
            var value = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            return string.IsNullOrWhiteSpace(value) ? RuntimeInformation.OSDescription : value.Trim();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
