using System.Text;
using MdExplorer.Infrastructure.Ai;
using Xunit;

namespace MdExplorer.Tests;

public sealed class BertTextTokenizerTests
{
    private const string MinimalVocab = "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nhello\nworld\n!\n中\n文\n";

    [Theory]
    [InlineData("Hello\nworld")]
    [InlineData("Hello\r\nworld")]
    [InlineData("Hello\tworld")]
    [InlineData("Hello\0world")]
    public void ControlSeparatorsDoNotJoinAdjacentWords(string text)
    {
        var expected = Encode("Hello world");
        var actual = Encode(text);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CjkCharactersRemainIndividuallyTokenized()
    {
        var ids = Encode("中文");

        Assert.Equal(new[] { 8, 9 }, ids);
    }

    [Fact]
    public void NormalizerCapsInputByUnicodeScalarCount()
    {
        var normalized = BertTextTokenizer.Normalize(string.Concat(Enumerable.Repeat("界", 9_000)));

        var runeCount = 0;
        foreach (var _ in normalized.EnumerateRunes())
            runeCount++;
        Assert.Equal(8_192, runeCount);
    }

    private static IReadOnlyList<int> Encode(string text)
    {
        using var vocab = new MemoryStream(Encoding.UTF8.GetBytes(MinimalVocab));
        return BertTextTokenizer.Encode(vocab, text);
    }
}
