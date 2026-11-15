using System.Text.RegularExpressions;

namespace ScreenAssistant.Core.AIClient;

public static class MathText
{
    private static readonly Dictionary<string, string> Commands = new(StringComparer.Ordinal)
    {
        ["displaystyle"] = "", ["textstyle"] = "", ["left"] = "", ["right"] = "",
        ["in"] = "∈", ["notin"] = "∉", ["infty"] = "∞",
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥",
        ["ne"] = "≠", ["neq"] = "≠", ["pm"] = "±", ["mp"] = "∓",
        ["times"] = "×", ["cdot"] = "·", ["div"] = "÷",
        ["cup"] = "∪", ["cap"] = "∩", ["pi"] = "π", ["alpha"] = "α",
        ["beta"] = "β", ["gamma"] = "γ", ["to"] = "→"
    };

    public static string Format(string text)
    {
        return Regex.Replace(text, @"\\\(([^\r\n]*?)(?:\\\)|(?=\r?$))|\\\[([\s\S]*?)\\\]|\$\$([\s\S]*?)\$\$|(?<![\w$])\$(?!\d+[.,]?\d*\s*(?:$|[.,]))([^$\r\n]+)\$(?!\d)",
            match => FormatExpression(match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value),
            RegexOptions.Multiline);
    }

    private static string FormatExpression(string expression)
    {
        expression = Regex.Replace(expression, @"\\([a-zA-Z]+)([ \t]?)", match =>
            Commands.TryGetValue(match.Groups[1].Value, out var symbol)
                ? symbol + (char.IsLetterOrDigit(symbol.LastOrDefault()) ? match.Groups[2].Value : "")
                : match.Value);
        expression = Regex.Replace(expression, @"\\(?:dfrac|tfrac|frac)\s*\{([^{}]*)\}\s*\{([^{}]*)\}", "($1)/($2)");
        expression = Regex.Replace(expression, @"\\sqrt\s*\{([^{}]*)\}", "√($1)");
        expression = Regex.Replace(expression, @"\^\{([^{}]+)\}", "^($1)");
        return expression.Trim();
    }
}
