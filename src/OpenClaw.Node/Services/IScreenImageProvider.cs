using System.Threading.Tasks;

namespace OpenClaw.Node.Services
{
    public interface IScreenImageProvider
    {
        Task<(byte[] bytes, int width, int height)> CaptureScreenshotBytesAsync(int screenIndex = 0, string format = "png");
        Task<(byte[] bytes, int width, int height)> CaptureWindowBytesAsync(long handle, string format = "png");
    }
}
