using System.Threading.Tasks;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class CameraCaptureServiceTests
    {
        [Fact]
        public async Task ListDevicesAsync_ShouldReturnWellFormedEntries_WhenPresent()
        {
            var svc = new CameraCaptureService();
            var devices = await svc.ListDevicesAsync();

            Assert.NotNull(devices);

            foreach (var d in devices)
            {
                Assert.False(string.IsNullOrWhiteSpace(d.Id));
                Assert.False(string.IsNullOrWhiteSpace(d.Name));
                Assert.False(string.IsNullOrWhiteSpace(d.Position));
                Assert.False(string.IsNullOrWhiteSpace(d.DeviceType));
            }
        }

        [Fact]
        public async Task CaptureJpegAsBase64Async_ShouldReturnPayload_OnCurrentPlatform()
        {
            var svc = new CameraCaptureService();

            var (base64, width, height) = await svc.CaptureJpegAsBase64Async(
                facing: "front",
                maxWidth: 1280,
                quality: 0.9,
                delayMs: 1,
                deviceId: null);

            Assert.False(string.IsNullOrWhiteSpace(base64));
            Assert.True(width > 0);
            Assert.True(height > 0);
        }
    }
}
