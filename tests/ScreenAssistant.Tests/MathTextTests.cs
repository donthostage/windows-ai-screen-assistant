using ScreenAssistant.Core.AIClient;

namespace ScreenAssistant.Tests;

public class MathTextTests
{
    [Theory]
    [InlineData(@"Ответ: \(\displaystyle a \in[-2; 3]\)", "Ответ: a ∈[-2; 3]")]
    [InlineData(@"\[\frac{x+1}{x-2} \leq \sqrt{9}\]", "(x+1)/(x-2)≤√(9)")]
    [InlineData(@"Находим \(x \ge 5", "Находим x ≥5")]
    [InlineData("Цена — $15, текст без формул.", "Цена — $15, текст без формул.")]
    [InlineData(@"\[\sum_{i=1}^{n}", @"\[\sum_{i=1}^{n}")]
    [InlineData(@"\( \alpha + \beta \)", "α + β")]
    [InlineData(@"\(\frac{a}{b}\)", "(a)/(b)")]
    [InlineData(@"\(\left(x\right)\)", "(x)")]
    [InlineData(@"\(\pm 1\)", "±1")]
    [InlineData(@"\(\infty\)", "∞")]
    [InlineData(@"\(\neq\)", "≠")]
    [InlineData(@"\(\approx\)", "≈")]
    [InlineData(@"\(\times\)", "×")]
    [InlineData(@"\(\cdot\)", "·")]
    [InlineData(@"\(\int\)", "∫")]
    [InlineData(@"\(\prod\)", "∏")]
    [InlineData(@"\(\omega\)", "ω")]
    [InlineData(@"\(\frac{1+\frac{a}{b}}{c}\)", "(1+(a)/(b))/(c)")]
    [InlineData(@"$$x^2$$", "x^2")]
    public void FormatsMathWrappers(string input, string expected) =>
        Assert.Equal(expected, MathText.Format(input));
}
