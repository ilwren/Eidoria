using System.Text;
using Microsoft.ML.Tokenizers;

namespace MdExplorer.Infrastructure.Ai;

/// <summary>Shared BERT tokenizer setup and bounded normalizer for model inference and tokenizer regression tests.</summary>
internal static class BertTextTokenizer
{
    private const int MaxInputRunes = 8192;

    public static BertTokenizer Create(Stream vocabStream)
    {
        ArgumentNullException.ThrowIfNull(vocabStream);
        return BertTokenizer.Create(vocabStream, new BertOptions
        {
            ApplyBasicTokenization = true,
            LowerCaseBeforeTokenization = true,
            IndividuallyTokenizeCjk = true,
            RemoveNonSpacingMarks = true,
            UnknownToken = "[UNK]",
            ClassificationToken = "[CLS]",
            SeparatorToken = "[SEP]",
            PaddingToken = "[PAD]",
        });
    }

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(Math.Min(text.Length, MaxInputRunes));
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count++ >= MaxInputRunes)
                break;
            // Explicitly separate words around LF/CR/TAB and other controls instead of letting
            // tokenizer-specific basic-tokenization paths silently concatenate the neighboring text.
            builder.Append(Rune.IsControl(rune) ? ' ' : rune.ToString());
        }
        return builder.ToString();
    }

    internal static IReadOnlyList<int> Encode(Stream vocabStream, string text)
    {
        var tokenizer = Create(vocabStream);
        return tokenizer.EncodeToIds(Normalize(text), addSpecialTokens: false);
    }
}
