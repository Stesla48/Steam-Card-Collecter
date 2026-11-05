using QRCoder;

namespace SteamCardCollector.App;

static class QrCodes
{
    public static byte[] Png(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(8);
    }
}
