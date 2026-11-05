using System.Text;
using MdExplorer.Infrastructure.Ai;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>Runs the production tokenizer/session/pooling path against a tiny deterministic ONNX fixture.</summary>
public sealed class OnnxTextEmbeddingTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(
        Path.GetTempPath(), "mdexplorer-onnx-embedding-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CpuSessionProducesUnitNormalizedEmbeddingFromLocalBertVocab()
    {
        var modelRoot = Path.Combine(_baseDir, "models", "text-embed-en");
        Directory.CreateDirectory(Path.Combine(modelRoot, "onnx"));
        File.WriteAllText(
            Path.Combine(modelRoot, "vocab.txt"),
            "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nhello\nworld\n!\n",
            new UTF8Encoding(false));

        using (var fixture = typeof(OnnxTextEmbeddingTests).Assembly
                   .GetManifestResourceStream("MdExplorer.Tests.Fixtures.mini-bert.onnx")
               ?? throw new InvalidOperationException("Missing tiny ONNX embedding fixture."))
        using (var output = File.Create(Path.Combine(modelRoot, "onnx", "model_quantized.onnx")))
            fixture.CopyTo(output);

        var model = new AiModelDefinition(
            "text-embed-en", "Fixture MiniLM", "test only", "Apache-2.0", string.Empty,
            "test fixture", "fixture-revision", "onnx", 1, true,
            ["vocab.txt", "onnx/model_quantized.onnx"]);
        var catalog = new AiModelCatalog(Path.Combine(_baseDir, "models"), [model]);
        using var service = new OnnxTextEmbeddingService(catalog);

        var available = Assert.Single(service.GetAvailableModels());
        Assert.Equal(384, available.Dimensions);
        var embedding = service.Embed("text-embed-en", "hello", isQuery: false, CancellationToken.None);

        Assert.Equal(384, embedding.Length);
        var norm = Math.Sqrt(embedding.Sum(value => value * value));
        Assert.InRange(norm, 0.99999, 1.00001);
        Assert.All(embedding, value => Assert.InRange(value, 0.05f, 0.06f));
    }
}
