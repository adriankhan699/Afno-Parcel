using NepalParcel.Domain.Common;

namespace NepalParcel.UnitTests.Domain;

public sealed class NepalPhoneNumberTests
{
    [Theory]
    [InlineData("9841234567", "9841234567")]
    [InlineData("+977 984-123-4567", "9841234567")]
    [InlineData("9779801234567", "9801234567")]
    [InlineData("9612345678", "9612345678")]
    [InlineData("01-4123456", "014123456")]
    [InlineData("061-523456", "061523456")]
    public void TryNormalize_ValidNumbers(string input, string expected)
    {
        Assert.True(NepalPhoneNumber.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("9541234567")]
    [InlineData("98412345678")]
    [InlineData("abcdefghij")]
    public void IsValid_RejectsInvalidNumbers(string? input)
    {
        Assert.False(NepalPhoneNumber.IsValid(input));
    }
}
