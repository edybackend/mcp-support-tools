using System.Text.RegularExpressions;

namespace SupportDesk.Mcp.Security;

/// <summary>
/// What an AI client may see and write. Personal data is masked on the way out; card numbers and secrets are refused
/// on the way in. The model never needs a full email or phone number to triage a ticket.
/// </summary>
public static partial class Redaction
{
    public static string MaskEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }

    public static string MaskPhone(string phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        var digits = new string([.. phone.Where(char.IsAsciiDigit)]);
        return digits.Length < 4 ? "***" : $"***-{digits[^4..]}";
    }

    /// <summary>Finds a sequence of 13–19 digits (spaces/dashes allowed) that passes the Luhn check.</summary>
    public static bool ContainsCardNumber(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match match in CardCandidate().Matches(text))
        {
            var digits = new string([.. match.Value.Where(char.IsAsciiDigit)]);
            if (digits.Length is >= 13 and <= 19 && Luhn(digits))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Looks like a credential: API keys, bearer tokens, passwords written inline.</summary>
    public static bool ContainsSecret(string text) => SecretPattern().IsMatch(text);

    /// <summary>For the audit log: long values are cut and anything that looks like a card or a secret is replaced.</summary>
    public static string ForAudit(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var redacted = CardCandidate().Replace(value, m => ContainsCardNumber(m.Value) ? "[card]" : m.Value);
        redacted = SecretPattern().Replace(redacted, "[secret]");
        return redacted.Length <= 200 ? redacted : redacted[..200] + "…";
    }

    private static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"\b\d(?:[ -]?\d){12,18}\b")]
    private static partial Regex CardCandidate();

    [GeneratedRegex(@"(?i)\b(?:password|passwd|secret|api[_-]?key|token)\s*[:=]\s*[^\s"",;}]+|\bsk-[A-Za-z0-9]{16,}|\bBearer\s+[A-Za-z0-9._-]{20,}")]
    private static partial Regex SecretPattern();
}
