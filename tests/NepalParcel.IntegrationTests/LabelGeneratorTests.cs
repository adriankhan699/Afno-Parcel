using System.Globalization;
using System.Text.RegularExpressions;
using NepalParcel.Domain.Parcels;
using NepalParcel.Infrastructure.Labels;
using ZXing;
using ZXing.Common;

namespace NepalParcel.IntegrationTests;

/// <summary>
/// The labels must really scan. These tests draw the generated SVG into a grid of pixels and read it back with ZXing's decoder,
/// which is what a warehouse scanner does with a printed label.
/// </summary>
public sealed partial class LabelGeneratorTests
{
    private readonly ZXingLabelGenerator _generator = new();

    [GeneratedRegex("""<rect x="(?<x>\d+)" y="(?<y>\d+)" width="(?<w>\d+)" height="(?<h>\d+)"\s*/>""")]
    private static partial Regex RectPattern();

    [GeneratedRegex("""viewBox="0 0 (?<w>\d+) (?<h>\d+)" """)]
    private static partial Regex ViewBoxPattern();

    private static Result? Decode(string svg, BarcodeFormat format)
    {
        var box = ViewBoxPattern().Match(svg);
        Assert.True(box.Success, "The SVG has no viewBox.");
        var width = int.Parse(box.Groups["w"].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(box.Groups["h"].Value, CultureInfo.InvariantCulture);

        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255); // white paper
        foreach (Match rect in RectPattern().Matches(svg))
        {
            var x = int.Parse(rect.Groups["x"].Value, CultureInfo.InvariantCulture);
            var y = int.Parse(rect.Groups["y"].Value, CultureInfo.InvariantCulture);
            var w = int.Parse(rect.Groups["w"].Value, CultureInfo.InvariantCulture);
            var h = int.Parse(rect.Groups["h"].Value, CultureInfo.InvariantCulture);
            for (var row = y; row < y + h && row < height; row++)
            {
                for (var column = x; column < x + w && column < width; column++)
                {
                    pixels[(row * width) + column] = 0; // black ink
                }
            }
        }

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions { PossibleFormats = [format], TryHarder = true },
        };
        return reader.Decode(new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8));
    }

    [Theory]
    [InlineData("AFNK7M2Q9XT4B3")]
    [InlineData("AFN2222222222B")]
    public void TheBarcode_ScansBackToTheTrackingNumber(string trackingNumber)
    {
        var result = Decode(_generator.CreateBarcodeSvg(trackingNumber), BarcodeFormat.CODE_128);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.CODE_128, result.BarcodeFormat);
        Assert.Equal(trackingNumber, result.Text);
    }

    [Fact]
    public void TheBarcode_OfARealGeneratedNumber_ScansBack_AndEveryNumberDrawsDifferently()
    {
        var numbers = Enumerable.Range(0, 20).Select(_ => TrackingNumbers.Generate()).Distinct().ToList();

        foreach (var number in numbers)
        {
            Assert.Equal(number, Decode(_generator.CreateBarcodeSvg(number), BarcodeFormat.CODE_128)?.Text);
        }

        Assert.Equal(numbers.Count, numbers.Select(_generator.CreateBarcodeSvg).Distinct().Count());
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("https://afnoparcel.example.com")]
    public void TheQrCode_ScansBackToTheTrackingLink_AndTheLinkYieldsTheTrackingNumber(string baseUrl)
    {
        var trackingNumber = TrackingNumbers.Generate();
        var link = $"{baseUrl}/track/{trackingNumber}";

        var result = Decode(_generator.CreateQrSvg(link), BarcodeFormat.QR_CODE);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.QR_CODE, result.BarcodeFormat);
        Assert.Equal(link, result.Text);
        Assert.True(TrackingNumbers.TryExtract(result.Text, out var number));
        Assert.Equal(trackingNumber, number);
    }

    [Fact]
    public void TheLabelsAreSvgImages_WithNoScriptsOrExternalReferences()
    {
        var svgs = new[] { _generator.CreateBarcodeSvg("AFNK7M2Q9XT4B3"), _generator.CreateQrSvg("http://localhost:5173/track/AFNK7M2Q9XT4B3") };

        foreach (var svg in svgs)
        {
            Assert.Contains("<svg", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onload", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("xlink:href", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<image", svg, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsDrawn_ForEmptyText(string text)
    {
        Assert.Throws<ArgumentException>(() => _generator.CreateBarcodeSvg(text));
        Assert.Throws<ArgumentException>(() => _generator.CreateQrSvg(text));
    }
}
