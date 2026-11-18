using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.UnitTests.Domain;

public sealed class TrackingNumberAndTimeTests
{
    [Fact]
    public void Generate_ProducesValidFormat()
    {
        var number = TrackingNumbers.Generate();

        Assert.Equal(TrackingNumbers.Length, number.Length);
        Assert.StartsWith("AFN", number, StringComparison.Ordinal);
        Assert.True(TrackingNumbers.IsValid(number));
        Assert.DoesNotContain(number, c => "01OIL".Contains(c, StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_IsUniqueAcrossManyCalls()
    {
        var numbers = Enumerable.Range(0, 20_000).Select(_ => TrackingNumbers.Generate()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(20_000, numbers.Count);
    }

    [Fact]
    public void IsValid_AcceptsTypedVariants_AndRejectsTypos()
    {
        var number = TrackingNumbers.Generate();
        var typed = $" {number[..3].ToLowerInvariant()}-{number[3..7]} {number[7..]} ";
        var chars = number.ToCharArray();
        chars[5] = chars[5] == 'A' ? 'B' : 'A';

        Assert.True(TrackingNumbers.IsValid(typed));
        Assert.False(TrackingNumbers.IsValid(new string(chars)));
        Assert.False(TrackingNumbers.IsValid("AFN"));
        Assert.False(TrackingNumbers.IsValid(null));
    }

    [Fact]
    public void NepalTime_ConvertsUtcToKathmandu_PlusFiveFortyFive()
    {
        var utc = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

        var local = NepalTime.ToLocal(utc);

        Assert.Equal(new TimeSpan(5, 45, 0), local.Offset);
        Assert.Equal(new DateTime(2026, 11, 17, 5, 45, 0), local.DateTime);
        Assert.Equal("2026-11-17 05:45 NPT", NepalTime.Format(utc));
    }

    [Fact]
    public void NepalTime_HasNoDaylightSaving()
    {
        var summer = NepalTime.ToLocal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var winter = NepalTime.ToLocal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(summer.Offset, winter.Offset);
    }
}
