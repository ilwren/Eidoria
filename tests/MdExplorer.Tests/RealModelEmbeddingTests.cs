using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Infrastructure.Ai;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>Opt-in integration tests that execute the pinned, downloaded production ONNX weights.</summary>
public sealed class RealModelEmbeddingTests
{
    [Fact]
    public void BgeChineseEmbedsRelatedTextCloserThanUnrelatedText()
    {
        EnsureRealModelTestsEnabled();
        AssertSemanticRankings(
            "text-embed-zh",
            expectedDimensions: 512,
            ("如何重置账户密码", "忘记登录密码后，可以通过邮箱链接重设账户密码。", "厨房里用番茄和鸡蛋制作一道家常菜。"),
            ("如何申请退货退款", "收到商品后可以提交退货申请，审核通过会按原支付方式退款。", "周末去公园散步并观察天气变化。"),
            ("做番茄炒蛋需要什么", "先将鸡蛋打散，再与番茄一起翻炒并加入少许盐。", "忘记密码时使用邮箱链接重新设置账户。"));
    }

    [Fact]
    public void MiniLmEnglishEmbedsRelatedTextCloserThanUnrelatedText()
    {
        EnsureRealModelTestsEnabled();
        AssertSemanticRankings(
            "text-embed-en",
            expectedDimensions: 384,
            ("How can I reset my account password?", "If you forgot your login password, use the email link to reset your account.", "Chop tomatoes and eggs to make a quick dinner."),
            ("How do I request a refund for a returned item?", "Submit a return request after delivery; approved refunds go back to the original payment method.", "Take a walk in the park and check the weekend weather."),
            ("What ingredients do I need for tomato and egg stir-fry?", "Beat the eggs, stir-fry them with tomatoes, and add a little salt.", "Use the email link to reset a forgotten account password."));
    }

    [Fact]
    public async Task RealModelsRankLocalFixtureFilesByNaturalLanguageIntent()
    {
        EnsureRealModelTestsEnabled();
        var modelsDirectory = Environment.GetEnvironmentVariable(AiModelCatalog.DirectoryEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(modelsDirectory),
            $"Set {AiModelCatalog.DirectoryEnvironmentVariable} to the downloaded model directory.");

        var baseDirectory = Path.Combine(Path.GetTempPath(), "mdexplorer-real-semantic-search", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(baseDirectory, "files");
        var dataDirectory = Path.Combine(baseDirectory, "data");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var fixtures = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["account-password.txt"] = "忘记登录密码后，可使用邮箱链接重新设置账号密码。 Password reset instructions use an email link.",
                ["refund-policy.txt"] = "提交退货申请并审核通过后，退款会返回原支付方式。 Refunds are sent after a return is approved.",
                ["tomato-egg-recipe.txt"] = "将鸡蛋打散，与番茄一起翻炒并加入少许盐。 Tomato and egg stir-fry recipe.",
                ["weekend-weather.txt"] = "周末天气晴朗，适合到公园散步。 The weekend forecast is sunny, good for a walk.",
            };
            foreach (var (name, content) in fixtures)
                await File.WriteAllTextAsync(Path.Combine(root, name), content, TestContext.Current.CancellationToken);

            var settings = SettingsStore.LoadOrDefault(dataDirectory);
            settings.Settings.EnableLocalAi = true;
            settings.Settings.UseSearchIndex = true;
            settings.Settings.SearchIndexRootPaths = [root];
            var fileIndex = new FileNameIndexService(dataDirectory, settings);
            await fileIndex.RebuildAsync(null, TestContext.Current.CancellationToken);

            using var embeddings = new OnnxTextEmbeddingService(new AiModelCatalog(modelsDirectory!));
            var semantic = new SemanticSearchService(dataDirectory, fileIndex, settings, embeddings);
            await semantic.RebuildAsync(null, TestContext.Current.CancellationToken);

            var cases = new[]
            {
                ("请找出可以通过邮箱重设账号登录密码的说明", "account-password.txt"),
                ("退款多久到账，怎么申请退货", "refund-policy.txt"),
                ("番茄和鸡蛋一起炒的菜谱", "tomato-egg-recipe.txt"),
                ("How can I change a forgotten login password?", "account-password.txt"),
                ("How long does a refund take after returning an item?", "refund-policy.txt"),
                ("Find a simple tomato and egg recipe", "tomato-egg-recipe.txt"),
            };
            foreach (var (query, expectedName) in cases)
            {
                var hits = await semantic.SearchAsync(query, null, 4, TestContext.Current.CancellationToken);
                Assert.NotEmpty(hits);
                Assert.Equal(expectedName, hits[0].Item.Name);
            }
        }
        finally
        {
            try { Directory.Delete(baseDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void EnsureRealModelTestsEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MDEXPLORER_RUN_REAL_MODEL_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Skip("Set MDEXPLORER_RUN_REAL_MODEL_TESTS=1 and download the pinned models to run real-weight integration tests.");
        }
    }

    private static void AssertSemanticRankings(
        string modelId,
        int expectedDimensions,
        params (string Query, string Relevant, string Unrelated)[] cases)
    {
        var modelsDirectory = Environment.GetEnvironmentVariable(AiModelCatalog.DirectoryEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(modelsDirectory),
            $"Set {AiModelCatalog.DirectoryEnvironmentVariable} to the downloaded model directory.");

        using var service = new OnnxTextEmbeddingService(new AiModelCatalog(modelsDirectory!));
        Assert.Contains(service.GetAvailableModels(), model => model.Id == modelId);

        foreach (var (query, relevantDocument, unrelatedDocument) in cases)
        {
            var queryVector = service.Embed(modelId, query, isQuery: true, CancellationToken.None);
            var relevantVector = service.Embed(modelId, relevantDocument, isQuery: false, CancellationToken.None);
            var unrelatedVector = service.Embed(modelId, unrelatedDocument, isQuery: false, CancellationToken.None);

            AssertEmbedding(queryVector, expectedDimensions, modelId, "query");
            AssertEmbedding(relevantVector, expectedDimensions, modelId, "relevant document");
            AssertEmbedding(unrelatedVector, expectedDimensions, modelId, "unrelated document");

            var relevantScore = CosineSimilarity(queryVector, relevantVector);
            var unrelatedScore = CosineSimilarity(queryVector, unrelatedVector);
            Assert.True(
                relevantScore > unrelatedScore,
                $"{modelId} ranked unrelated text above the matching text for query '{query}': " +
                $"relevant={relevantScore:F4}, unrelated={unrelatedScore:F4}.");
        }
    }

    private static void AssertEmbedding(float[] embedding, int dimensions, string modelId, string inputName)
    {
        Assert.Equal(dimensions, embedding.Length);
        Assert.All(embedding, value => Assert.True(float.IsFinite(value), $"{modelId} emitted a non-finite value for {inputName}."));
        var norm = Math.Sqrt(embedding.Sum(value => (double)value * value));
        Assert.InRange(norm, 0.999, 1.001);
    }

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        Assert.Equal(left.Count, right.Count);
        var dot = 0d;
        for (var i = 0; i < left.Count; i++)
            dot += left[i] * right[i];
        return dot;
    }
}
