using System.Text.RegularExpressions;

namespace ZennoVibe.MailClient.Core;

/// <summary>
/// Pulls the one-time code out of a message so it can be copied without hunting for it
/// in the body text.
/// </summary>
internal static partial class VerificationCode
{
    private const int MaxScannedChars = 8_000;

    [GeneratedRegex(
        @"(?:code|otp|pin|password|verification|verify|token)\D{0,40}?(\d{4,8})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabelledCodeRegex();

    [GeneratedRegex(
        @"(\d{4,8})\D{0,40}?(?:is your|as your|verification|confirmation)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLabelRegex();

    [GeneratedRegex(@"\b\d{4,8}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AnyDigitsRegex();

    /// <summary>Returns the most likely one-time code, or null when nothing looks like one.</summary>
    public static string? Find(string? subject, string? body)
    {
        // The subject usually carries the code when it is present at all, so it wins.
        foreach (var text in new[] { subject, Shorten(body) })
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var match = LabelledCodeRegex().Match(text);
            if (match.Success)
                return match.Groups[1].Value;

            match = TrailingLabelRegex().Match(text);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return FindUnlabelled(subject) ?? FindUnlabelled(Shorten(body));
    }

    private static string? FindUnlabelled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string? fallback = null;

        foreach (var match in AnyDigitsRegex().Matches(text).Cast<Match>())
        {
            var value = match.Value;

            // Six digits is the common OTP length; take it as soon as it shows up.
            if (value.Length == 6)
                return value;

            if (fallback == null && !LooksLikeYear(value))
                fallback = value;
        }

        return fallback;
    }

    private static bool LooksLikeYear(string value) =>
        value.Length == 4 &&
        int.TryParse(value, out var number) &&
        number is >= 1900 and <= 2100;

    private static string Shorten(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return "";

        return body.Length > MaxScannedChars ? body[..MaxScannedChars] : body;
    }
}
