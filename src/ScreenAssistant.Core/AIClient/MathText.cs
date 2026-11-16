using System.Text;
using System.Text.RegularExpressions;

namespace ScreenAssistant.Core.AIClient;

public static class MathText
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Dictionary<string, string> Commands = new(StringComparer.Ordinal)
    {
        ["displaystyle"] = "", ["textstyle"] = "", ["text"] = "",
        ["left"] = "", ["right"] = "",
        ["in"] = "∈", ["notin"] = "∉", ["infty"] = "∞",
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥",
        ["ne"] = "≠", ["neq"] = "≠", ["approx"] = "≈",
        ["pm"] = "±", ["mp"] = "∓",
        ["times"] = "×", ["cdot"] = "·", ["div"] = "÷",
        ["cup"] = "∪", ["cap"] = "∩",
        ["sum"] = "∑", ["int"] = "∫", ["prod"] = "∏",
        ["pi"] = "π", ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ",
        ["delta"] = "δ", ["epsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η",
        ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ",
        ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["omicron"] = "ο",
        ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ",
        ["phi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["to"] = "→",
        ["mathbb"] = "", ["mathcal"] = ""
    };

    private static readonly Regex Wrapper = new(
        @"\\\(([^\r\n]*?)(?:\\\)|(?=\r?$))|"
        + @"\\\[([\s\S]{0,4000}?)\\\]|"
        + @"\$\$([\s\S]{0,4000}?)\$\$|"
        + @"(?<![\w$])\$(?!\d+[.,]?\d*\s*(?:$|[.,]))([^$\r\n]+)\$(?!\d)",
        RegexOptions.Compiled | RegexOptions.Multiline,
        MatchTimeout);

    private static readonly Regex Command = new(
        @"\\([a-zA-Z]+)([ \t]?)",
        RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex Sqrt = new(
        @"\\sqrt\s*\{([^{}]*)\}",
        RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex Power = new(
        @"\^\{([^{}]+)\}",
        RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex LeftRight = new(
        @"\\left([(\[{|.]?)|\\right([)\]}|.]?)",
        RegexOptions.Compiled,
        MatchTimeout);

    public static string Format(string text)
    {
        try
        {
            // Незакрытые \[ / $$ длиннее лимита оставляем как есть — Wrapper не матчит без закрытия.
            return Wrapper.Replace(text, match =>
            {
                var group = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success);
                if (group is null || !group.Success)
                    return match.Value;
                return FormatExpression(group.Value);
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    private static string FormatExpression(string expression)
    {
        try
        {
            expression = LeftRight.Replace(expression, m =>
            {
                if (m.Groups[1].Success)
                    return m.Groups[1].Value is "." or "" ? "" : m.Groups[1].Value;
                if (m.Groups[2].Success)
                    return m.Groups[2].Value is "." or "" ? "" : m.Groups[2].Value;
                return "";
            });
            expression = Command.Replace(expression, match =>
                Commands.TryGetValue(match.Groups[1].Value, out var symbol)
                    ? symbol + (char.IsLetterOrDigit(symbol.LastOrDefault()) ? match.Groups[2].Value : "")
                    : match.Value);
            expression = ReplaceFractions(expression);
            expression = Sqrt.Replace(expression, "√($1)");
            expression = Power.Replace(expression, "^($1)");
            return expression.Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return expression;
        }
    }

    private static string ReplaceFractions(string expression)
    {
        var result = new StringBuilder(expression.Length);
        var i = 0;
        while (i < expression.Length)
        {
            if (TryReadCommand(expression, i, out var cmdEnd, out var name)
                && name is "frac" or "dfrac" or "tfrac"
                && TryReadBraceGroup(expression, cmdEnd, out var num, out var afterNum)
                && TryReadBraceGroup(expression, afterNum, out var den, out var afterDen))
            {
                result.Append('(').Append(ReplaceFractions(num)).Append(")/(")
                    .Append(ReplaceFractions(den)).Append(')');
                i = afterDen;
                continue;
            }

            result.Append(expression[i]);
            i++;
        }

        return result.ToString();
    }

    private static bool TryReadCommand(string text, int index, out int end, out string name)
    {
        end = index;
        name = "";
        if (index >= text.Length || text[index] != '\\')
            return false;
        var i = index + 1;
        while (i < text.Length && char.IsLetter(text[i]))
            i++;
        if (i == index + 1)
            return false;
        name = text[(index + 1)..i];
        end = i;
        while (end < text.Length && char.IsWhiteSpace(text[end]))
            end++;
        return true;
    }

    private static bool TryReadBraceGroup(string text, int index, out string inner, out int end)
    {
        inner = "";
        end = index;
        if (index >= text.Length || text[index] != '{')
            return false;

        var depth = 0;
        for (var i = index; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    inner = text[(index + 1)..i];
                    // Один уровень вложенности внутри уже в inner; второй проход ReplaceFractions покроет вложенный frac.
                    end = i + 1;
                    while (end < text.Length && char.IsWhiteSpace(text[end]))
                        end++;
                    return true;
                }
            }
        }

        return false;
    }
}
