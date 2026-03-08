using System;
using OpenClaw.Node.Tray;
using Xunit;

namespace OpenClaw.Node.Tests
{
    public class TrayStatusBroadcasterTests
    {
        [Fact]
        public void Set_ShouldUpdateCurrentAndFireEvent()
        {
            var now = new DateTimeOffset(2026, 2, 23, 20, 15, 0, TimeSpan.Zero);
            var broadcaster = new TrayStatusBroadcaster(() => now);

            TrayStatusSnapshot? observed = null;
            broadcaster.OnStatusChanged += s => observed = s;

            broadcaster.Set(NodeRuntimeState.Connected, "Connected to Gateway");

            Assert.NotNull(observed);
            Assert.Equal(NodeRuntimeState.Connected, observed!.State);
            Assert.Equal("Connected to Gateway", observed.Message);
            Assert.Equal(now, observed.UpdatedAtUtc);
            Assert.Equal(observed, broadcaster.Current);
        }

        [Fact]
        public void Set_WithBlankMessage_ShouldFallbackToStateName()
        {
            var broadcaster = new TrayStatusBroadcaster(() => DateTimeOffset.UnixEpoch);

            broadcaster.Set(NodeRuntimeState.Reconnecting, "   ");

            Assert.Equal("Reconnecting", broadcaster.Current.Message);
        }
    }
}
