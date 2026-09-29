using VoiceBridge.Core.Reconstruction;

namespace VoiceBridge.Tests;

public sealed class ParticipantNormalizerTests
{
    [Theory]
    [InlineData("+1 (555) 123-4567", "+15551234567")]
    [InlineData(" 555.123.4567 ", "5551234567")]
    [InlineData("001-23-45", "0012345")]
    public void RemovesPhoneFormattingWithoutAddingOrRemovingCountryPrefix(string raw, string expected)
    {
        Assert.Equal(expected, ParticipantNormalizer.NormalizePhoneNumber(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+1 800 555 1212 ext. 3")]
    [InlineData("555-ABCD")]
    [InlineData("+1234567890123456")]
    public void LeavesUnparseableOrMissingPhoneNumbersUnknown(string? raw)
    {
        Assert.Null(ParticipantNormalizer.NormalizePhoneNumber(raw));
    }

    [Fact]
    public void NormalizesDisplayNameWhitespaceAndCase()
    {
        const string raw = "  Alex\t Smith  ";

        var normalized = ParticipantNormalizer.NormalizeDisplayName(raw);

        Assert.Equal("alex smith", normalized);
    }
}
