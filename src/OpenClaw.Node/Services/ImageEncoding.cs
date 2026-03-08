using System;
using System.IO;

#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
#endif

namespace OpenClaw.Node.Services
{
    public static class ImageEncoding
    {
        public sealed class EncodedImage
        {
            public string Base64 { get; set; } = string.Empty;
            public int Width { get; set; }
            public int Height { get; set; }
            public int Bytes { get; set; }
            public string MimeType { get; set; } = "image/jpeg";
            public string Format { get; set; } = "jpg";
        }

        public static EncodedImage EncodeJpegBase64(byte[] imageBytes, int maxWidth = 1600, double quality = 0.85)
        {
#if WINDOWS
            if (imageBytes == null || imageBytes.Length == 0) return new EncodedImage();
            maxWidth = Math.Clamp(maxWidth, 64, 8000);
            quality = Math.Clamp(quality, 0.1, 1.0);

            using var input = new MemoryStream(imageBytes);
            using var srcImage = Image.FromStream(input);

            var srcW = srcImage.Width;
            var srcH = srcImage.Height;

            var outW = srcW;
            var outH = srcH;
            if (srcW > maxWidth)
            {
                var scale = (double)maxWidth / srcW;
                outW = maxWidth;
                outH = Math.Max(1, (int)Math.Round(srcH * scale));
            }

            using var bmp = new Bitmap(outW, outH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(srcImage, 0, 0, outW, outH);
            }

            using var output = new MemoryStream();
            var jpgEncoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.MimeType == "image/jpeg");
            if (jpgEncoder != null)
            {
                using var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Round(quality * 100));
                bmp.Save(output, jpgEncoder, encParams);
            }
            else
            {
                bmp.Save(output, ImageFormat.Jpeg);
            }

            var bytes = output.ToArray();
            return new EncodedImage
            {
                Base64 = Convert.ToBase64String(bytes),
                Width = outW,
                Height = outH,
                Bytes = bytes.Length,
                MimeType = "image/jpeg",
                Format = "jpg",
            };
#else
            return new EncodedImage();
#endif
        }
    }
}
