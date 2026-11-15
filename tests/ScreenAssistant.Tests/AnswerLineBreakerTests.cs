using ScreenAssistant.Overlay;

namespace ScreenAssistant.Tests;

public class AnswerLineBreakerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a ∈ [−2; 3]")]
    [InlineData("Короткий ответ.\r\nВторая строка.")]
    public void LeavesTextThatFitsUntouched(string? text)
    {
        var wrapped = AnswerLineBreaker.Apply(text, line => line.Length <= 80);

        Assert.Equal(text ?? "", wrapped);
        Assert.Equal(text ?? "", AnswerLineBreaker.Strip(wrapped));
    }

    [Fact]
    public void BreaksLongFormulaAfterOperator()
    {
        var wrapped = AnswerLineBreaker.Apply("(x+1)/(x-2)", line => line.Length <= 6);

        Assert.Equal("(x+1)/\u200B(x-2)", wrapped);
        Assert.Equal("(x+1)/(x-2)", AnswerLineBreaker.Strip(wrapped));
        Assert.All(Pieces(wrapped), piece => Assert.True(piece.Length <= 6));
    }

    [Fact]
    public void WrapsAtSpacesWithoutSoftBreak()
    {
        var wrapped = AnswerLineBreaker.Apply("hello world", line => line.Length <= 8);

        Assert.Equal("hello world", wrapped);
    }

    [Fact]
    public void BreaksUnbrokenTokenAndKeepsExplicitLines()
    {
        var wrapped = AnswerLineBreaker.Apply("abcdefghi\n123456789xyz", line => line.Length <= 6);

        Assert.Equal("abcdefghi\n123456789xyz", AnswerLineBreaker.Strip(wrapped));
        Assert.All(Pieces(wrapped), piece => Assert.True(piece.Length <= 6));
        Assert.Contains("\n", wrapped);
    }

    [Fact]
    public void DoesNotSplitSurrogatePairs()
    {
        var text = "😀1234567890";
        var wrapped = AnswerLineBreaker.Apply(text, line => line.Length <= 4);

        Assert.Equal(text, AnswerLineBreaker.Strip(wrapped));
        Assert.DoesNotContain("\uD83D\u200B", wrapped);
        Assert.DoesNotContain("\u200B\uDE00", wrapped);
    }

    private static IEnumerable<string> Pieces(string wrapped) =>
        wrapped.Split('\u200B', '\n', '\r');
}
