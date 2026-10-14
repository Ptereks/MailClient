namespace ZennoVibe.MailClient.Core;

/// <summary>
/// Helpers for the display strings stored on messages, e.g. "Support &lt;help@example.com&gt;".
/// </summary>
internal static class MailAddressText
{
    /// <summary>
    /// Returns the bare address from a display string so copying gives something pasteable.
    /// Falls back to the trimmed input when no address can be isolated.
    /// </summary>
    public static string ExtractFirst(string displayText)
    {
        if (string.IsNullOrWhiteSpace(displayText))
            return "";

        var text = displayText.Trim();

        var open = text.IndexOf('<');
        var close = text.IndexOf('>', open + 1);
        if (open >= 0 && close > open + 1)
            return text[(open + 1)..close].Trim();

        // Comma-separated lists without angle brackets: take the first entry.
        var comma = text.IndexOf(',');
        if (comma > 0)
            text = text[..comma].Trim();

        return text;
    }
}
