using ScreenAssistant.Core.AIClient;

namespace ScreenAssistant.Tests;

public class MathTextTests
{
    [Theory]
    [InlineData(@"Ответ: \(\displaystyle a \in[-2; 3]\)", "Ответ: a ∈[-2; 3]")]
    [InlineData(@"\[\frac{x+1}{x-2} \leq \sqrt{9}\]", "(x+1)/(x-2) ≤√(9)")]
    [InlineData(@"Находим \(x \ge 5", "Находим x ≥5")]
    [InlineData("Цена — $15, текст без формул.", "Цена — $15, текст без формул.")]
    public void FormatsMathWrappers(string input, string expected) =>
        Assert.Equal(expected, MathText.Format(input));
}
