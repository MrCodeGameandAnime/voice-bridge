using System.Text;

namespace VoiceBridge.Core.Reconstruction;

public static class ParticipantNormalizer
{
    public static string? NormalizeDisplayName(string? rawDisplayName)
    {
        if (string.IsNullOrWhiteSpace(rawDisplayName))
        {
            return null;
        }

        var normalized = rawDisplayName.Normalize(NormalizationForm.FormKC);
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    public static string? NormalizePhoneNumber(string? rawPhoneNumber)
    {
        if (string.IsNullOrWhiteSpace(rawPhoneNumber))
        {
            return null;
        }

        var raw = rawPhoneNumber.Trim();
        var hasInternationalPrefix = raw[0] == '+';
        var digits = new StringBuilder(raw.Length);

        for (var index = hasInternationalPrefix ? 1 : 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character is >= '0' and <= '9')
            {
                digits.Append(character);
                continue;
            }

            if (character is ' ' or '\t' or '\r' or '\n' or '-' or '(' or ')' or '.')
            {
                continue;
            }

            return null;
        }

        if (digits.Length is < 3 or > 15)
        {
            return null;
        }

        var normalizedDigits = digits.ToString();
        return hasInternationalPrefix ? $"+{normalizedDigits}" : normalizedDigits;
    }
}
