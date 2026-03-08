using System;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Node.Services;
using System.Text.Json;

namespace OpenClaw.Node.Tests
{
    public class AutomationServiceTests
    {
        [Fact]
        public async Task GetSnapshotAsync_ReturnsValidJson()
        {
            var service = new AutomationService();
            var snapshot = await service.GetSnapshotAsync();
            
            Assert.NotNull(snapshot);
            // On Linux, it returns "{}". On Windows, it returns a JSON structure.
            // We verify it parses as JSON.
            var doc = JsonDocument.Parse(snapshot);
            Assert.NotNull(doc);
        }

        [Fact]
        public async Task FindUiElementDetailedAsync_ReturnsResult()
        {
            var service = new AutomationService();
            var result = await service.FindUiElementDetailedAsync(null, "Test", "Name", "Id", "Button");
            
            Assert.NotNull(result);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal("not-windows", result.Reason);
            }
            else
            {
                // On Windows, it might return "not-found" or "window-not-found" if the window doesn't exist
                Assert.True(result.Reason != null);
            }
        }
    }
}
