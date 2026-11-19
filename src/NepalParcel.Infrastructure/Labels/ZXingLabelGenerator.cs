using NepalParcel.Application.Network;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace NepalParcel.Infrastructure.Labels;

/// <summary>Draws Code 128 barcodes and QR codes as SVG images with ZXing.Net. Pure code: no fonts, no graphics libraries.</summary>
internal sealed class ZXingLabelGenerator : ILabelGenerator
{
    public string CreateBarcodeSvg(string text) =>
        Render(BarcodeFormat.CODE_128, text, new EncodingOptions { Width = 420, Height = 110, Margin = 10, PureBarcode = true });

    public string CreateQrSvg(string text) =>
        Render(
            BarcodeFormat.QR_CODE,
            text,
            new QrCodeEncodingOptions { Width = 300, Height = 300, Margin = 2, ErrorCorrection = ErrorCorrectionLevel.M, CharacterSet = "UTF-8" });

    private static string Render(BarcodeFormat format, string text, EncodingOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var writer = new BarcodeWriterSvg { Format = format, Options = options };
        return writer.Write(text).Content;
    }
}
