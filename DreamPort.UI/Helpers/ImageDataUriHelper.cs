using Avalonia.Media.Imaging;

namespace DreamPort.UI.Helpers;

/// <summary>将后端返回的 Base64 Data URI 图形验证码解码为 Avalonia 位图。</summary>
public static class ImageDataUriHelper
{
    public static Bitmap? TryDecodeRasterImage(string? dataUriOrBase64)
    {
        if (string.IsNullOrWhiteSpace(dataUriOrBase64))
            return null;

        var raw = dataUriOrBase64.Trim();
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = raw.IndexOf(',');
            if (commaIndex <= 0)
                return null;

            var header = raw[..commaIndex];
            if (header.Contains("image/svg", StringComparison.OrdinalIgnoreCase)
                || !header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                return null;

            raw = raw[(commaIndex + 1)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(raw);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
