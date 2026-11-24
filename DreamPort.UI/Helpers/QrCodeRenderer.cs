using Avalonia.Media.Imaging;
using QRCoder;

namespace DreamPort.UI.Helpers;

/// <summary>纯托管跨平台二维码生成器（供微软设备码等场景生成 Avalonia Bitmap）。</summary>
public static class QrCodeRenderer
{
    public static Bitmap? RenderToBitmap(string? content, int pixelsPerModule = 8)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            var qrCode = new PngByteQRCode(data);
            var pngBytes = qrCode.GetGraphic(Math.Clamp(pixelsPerModule, 2, 20));
            using var ms = new MemoryStream(pngBytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
