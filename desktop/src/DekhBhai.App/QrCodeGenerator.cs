using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace DekhBhai.App;

/// <summary>
/// Renders the viewer share link as a QR code for display inside the Dekh Bhai window only -
/// this bitmap never touches the captured desktop. Q-level error correction and an explicit
/// quiet zone are used specifically so a phone camera can scan it reliably off a screen (glare,
/// slight skew, moderate distance all being real conditions for that use case).
/// </summary>
internal static class QrCodeGenerator
{
    public static BitmapImage Generate(string content)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var pngQr = new PngByteQRCode(qrData);
        var pngBytes = pngQr.GetGraphic(pixelsPerModule: 8, drawQuietZones: true);

        var image = new BitmapImage();
        using var stream = new MemoryStream(pngBytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
