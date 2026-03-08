using System.Threading.Tasks;
using OpenClaw.Node.Services;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class ScreenCaptureServiceTests
    {
        [Fact]
        public async Task ListDisplaysAsync_ShouldReturnWellFormedEntries_WhenPresent()
        {
            var svc = new ScreenCaptureService();
            ScreenCaptureService.ScreenDisplayInfo[] displays;

            try
            {
                displays = await svc.ListDisplaysAsync();
            }
            catch (System.BadImageFormatException)
            {
                // Some environments cannot load ScreenRecorderLib display APIs; treat as acceptable here.
                return;
            }

            Assert.NotNull(displays);

            foreach (var d in displays)
            {
                Assert.True(d.Index >= 0);
                Assert.False(string.IsNullOrWhiteSpace(d.Id));
                Assert.False(string.IsNullOrWhiteSpace(d.Name));
            }
        }
    }
}
