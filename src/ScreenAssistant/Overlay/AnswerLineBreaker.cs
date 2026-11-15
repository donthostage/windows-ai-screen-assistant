using System.Text;

namespace ScreenAssistant.Overlay;

public static class AnswerLineBreaker
{
    public const char SoftBreak = '\u200B';

    public static string Apply(string? text, Func<string, bool> fits)
    {
        ArgumentNullException.ThrowIfNull(fits);
        if (string.IsNullOrEmpty(text))
            return "";

        var builder = new StringBuilder(text.Length);
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            if (!atEnd && text[i] is not ('\n' or '\r'))
                continue;

            WrapParagraph(text.AsSpan(start, i - start), fits, builder);
            if (atEnd)
                break;

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                builder.Append("\r\n");
                i++;
            }
            else
                builder.Append(text[i]);

            start = i + 1;
        }

        return builder.ToString();
    }

    public static string Strip(string? text) => (text ?? "").Replace(SoftBreak.ToString(), "");

    private static void WrapParagraph(ReadOnlySpan<char> paragraph, Func<string, bool> fits, StringBuilder builder)
    {
        if (paragraph.IsEmpty)
            return;

        var rest = paragraph.ToString();
        if (fits(rest))
        {
            builder.Append(rest);
            return;
        }

        while (rest.Length > 0)
        {
            if (fits(rest))
            {
                builder.Append(rest);
                return;
            }

            var best = 1;
            var low = 1;
            var high = rest.Length - 1;
            while (low <= high)
            {
                var mid = (low + high) >>> 1;
                if (fits(Prefix(rest, mid)))
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                    high = mid - 1;
            }

            var cut = AvoidSplit(rest, best);
            var opportunity = LastOpportunity(rest, cut);
            if (opportunity > 0)
                cut = opportunity;
            if (cut <= 0 || cut > rest.Length)
                cut = Math.Clamp(best, 1, rest.Length);

            builder.Append(rest, 0, cut);
            if (cut < rest.Length && !char.IsWhiteSpace(rest[cut - 1]))
                builder.Append(SoftBreak);

            rest = rest[cut..];
        }
    }

    private static string Prefix(string text, int length)
    {
        if (length < text.Length && char.IsHighSurrogate(text[length - 1]))
            length++;

        return text[..Math.Clamp(length, 0, text.Length)];
    }

    private static int AvoidSplit(string text, int length)
    {
        length = Math.Clamp(length, 1, text.Length);
        if (length < text.Length && char.IsHighSurrogate(text[length - 1]))
            return length > 1 ? length - 1 : Math.Min(text.Length, length + 1);

        return length;
    }

    private static int LastOpportunity(string text, int maxLength)
    {
        var limit = Math.Min(Math.Max(maxLength, 0), text.Length);
        for (var i = limit - 1; i >= 0; i--)
        {
            if (IsOpportunity(text[i]))
                return i + 1;
        }

        return -1;
    }

    private static bool IsOpportunity(char ch) =>
        char.IsWhiteSpace(ch) || ch is
            '+' or '-' or '−' or '–' or '—' or '=' or '/' or '*' or
            ',' or ';' or ':' or ')' or ']' or '}' or
            '≤' or '≥' or '≠' or '±' or '×' or '·' or
            '÷' or '→' or '∈' or '∉' or '≈' or '<' or '>' or '^';
}
