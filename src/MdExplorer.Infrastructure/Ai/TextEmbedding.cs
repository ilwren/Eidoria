using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace MdExplorer.Infrastructure.Ai;

/// <summary>已安装的文本嵌入器。Dimensions 是模型输出维数，Revision 与下载脚本的固定 SHA 对齐。</summary>
public sealed record TextEmbeddingModel(string Id, string Name, string Revision, int Dimensions);

/// <summary>可注入的端侧嵌入端口，便于在不下载权重的情况下测试索引与排序。</summary>
public interface ITextEmbeddingService
{
    IReadOnlyList<TextEmbeddingModel> GetAvailableModels();
    float[] Embed(string modelId, string text, bool isQuery, CancellationToken cancellationToken);
}

/// <summary>
/// CPU ONNX BERT embedding runner。运行时与 tokenizer 均 MIT；所有路径只从已安装模型清单目录解析，
/// 并且 Session 仅在用户请求语义索引/搜索时延迟创建。
/// </summary>
public sealed class OnnxTextEmbeddingService : ITextEmbeddingService, IDisposable
{
    private const int MaxTokens = 128;
    private const string BgeChineseId = "text-embed-zh";
    private const string MiniLmEnglishId = "text-embed-en";

    private readonly AiModelCatalog _catalog;
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelRunner> _runners = new(StringComparer.Ordinal);
    private bool _disposed;

    public OnnxTextEmbeddingService(AiModelCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public IReadOnlyList<TextEmbeddingModel> GetAvailableModels() =>
        _catalog.Scan()
            .Where(status => status.Installed && TryGetDimensions(status.Definition.Id, out _))
            .Select(status => new TextEmbeddingModel(
                status.Definition.Id,
                status.Definition.Name,
                status.Definition.Revision,
                GetDimensions(status.Definition.Id)))
            .ToArray();

    public float[] Embed(string modelId, string text, bool isQuery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var runner = GetOrCreateRunner(modelId);
        lock (runner.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return runner.Embed(text, isQuery, cancellationToken);
        }
    }

    private ModelRunner GetOrCreateRunner(string modelId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runners.TryGetValue(modelId, out var existing))
                return existing;

            var model = _catalog.Models.FirstOrDefault(item => item.Id == modelId)
                ?? throw new InvalidOperationException($"模型清单里没有文本嵌入模型：{modelId}");
            if (!TryGetDimensions(modelId, out var dimensions))
                throw new NotSupportedException($"暂不支持该文本模型：{modelId}");

            var modelRoot = Path.GetFullPath(Path.Combine(_catalog.ModelsDirectory, model.Id));
            var onnxPath = ResolveFile(modelRoot, model.Files.FirstOrDefault(file => file.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"模型清单缺少 ONNX 权重：{modelId}"));
            var vocabPath = ResolveFile(modelRoot, model.Files.FirstOrDefault(file =>
                file.Equals("vocab.txt", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"模型清单缺少 BERT vocab.txt：{modelId}"));
            if (!File.Exists(onnxPath) || !File.Exists(vocabPath))
                throw new FileNotFoundException($"文本模型 {modelId} 文件不完整，请重新运行模型下载脚本。");

            var runner = new ModelRunner(model, dimensions, onnxPath, vocabPath);
            _runners.Add(modelId, runner);
            return runner;
        }
    }

    private static string ResolveFile(string modelRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(modelRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = modelRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("模型清单路径越出模型目录。");
        return fullPath;
    }

    private static bool TryGetDimensions(string modelId, out int dimensions)
    {
        dimensions = modelId switch
        {
            BgeChineseId => 512,
            MiniLmEnglishId => 384,
            _ => 0,
        };
        return dimensions > 0;
    }

    private static int GetDimensions(string modelId) =>
        TryGetDimensions(modelId, out var dimensions) ? dimensions : 0;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var runner in _runners.Values)
                runner.Dispose();
            _runners.Clear();
        }
    }

    private sealed class ModelRunner : IDisposable
    {
        private readonly AiModelDefinition _model;
        private readonly InferenceSession _session;
        private readonly BertTokenizer _tokenizer;
        private readonly string _queryPrefix;
        private readonly int _expectedDimensions;

        public ModelRunner(AiModelDefinition model, int expectedDimensions, string onnxPath, string vocabPath)
        {
            _model = model;
            _expectedDimensions = expectedDimensions;
            _queryPrefix = model.Id == BgeChineseId ? "为这个句子生成表示以用于检索相关文章：" : string.Empty;

            using (var vocab = File.OpenRead(vocabPath))
                _tokenizer = BertTextTokenizer.Create(vocab);

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 4),
                InterOpNumThreads = 1,
                EnableCpuMemArena = true,
            };
            try
            {
                _session = new InferenceSession(onnxPath, options);
            }
            finally
            {
                options.Dispose();
            }

            if (!_session.InputMetadata.ContainsKey("input_ids") ||
                !_session.InputMetadata.ContainsKey("attention_mask"))
                throw new InvalidDataException($"{_model.Name} 的 ONNX 输入不包含 input_ids/attention_mask。");
        }

        public object Gate { get; } = new();

        public float[] Embed(string text, bool isQuery, CancellationToken cancellationToken)
        {
            var normalized = BertTextTokenizer.Normalize(text);
            if (isQuery && _queryPrefix.Length > 0)
                normalized = _queryPrefix + normalized;

            // Special tokens are added below, so ask the tokenizer for body tokens only.
            var pieces = _tokenizer.EncodeToIds(normalized, addSpecialTokens: false);
            var available = Math.Max(1, MaxTokens - 2);
            var length = Math.Min(pieces.Count, available);
            var ids = new long[length + 2];
            ids[0] = _tokenizer.ClassificationTokenId;
            for (var i = 0; i < length; i++)
                ids[i + 1] = pieces[i];
            ids[^1] = _tokenizer.SeparatorTokenId;
            var mask = Enumerable.Repeat(1L, ids.Length).ToArray();
            var tokenTypes = new long[ids.Length];

            var inputs = new List<NamedOnnxValue>(_session.InputMetadata.Count);
            foreach (var (name, metadata) in _session.InputMetadata)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedName = name.ToLowerInvariant();
                var values = normalizedName switch
                {
                    "input_ids" => ids,
                    "attention_mask" => mask,
                    "token_type_ids" => tokenTypes,
                    "position_ids" => Enumerable.Range(0, ids.Length).Select(value => (long)value).ToArray(),
                    _ => throw new InvalidDataException($"{_model.Name} 有不支持的必需输入：{name}"),
                };
                inputs.Add(CreateInput(name, metadata.ElementType, values));
            }

            using var outputs = _session.Run(inputs);
            cancellationToken.ThrowIfCancellationRequested();
            var output = outputs.FirstOrDefault(item => item.Name == "last_hidden_state") ?? outputs.FirstOrDefault();
            if (output is null)
                throw new InvalidDataException($"{_model.Name} 没有输出张量。");

            var tensor = output.AsTensor<float>();
            if (tensor.Rank != 3 || tensor.Dimensions[0] != 1 || tensor.Dimensions[1] < ids.Length)
                throw new InvalidDataException($"{_model.Name} 输出不是预期的 [1, seq, hidden] 张量。");
            var dimensions = tensor.Dimensions[2];
            if (dimensions != _expectedDimensions)
                throw new InvalidDataException($"{_model.Name} 输出维数为 {dimensions}，清单预期 {_expectedDimensions}。");

            var embedding = new float[dimensions];
            var tokenCount = 0;
            for (var token = 0; token < ids.Length; token++)
            {
                if (mask[token] == 0)
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                tokenCount++;
                for (var dimension = 0; dimension < dimensions; dimension++)
                    embedding[dimension] += tensor[0, token, dimension];
            }

            if (tokenCount == 0)
                throw new InvalidDataException("Tokenizer 没有生成任何有效 token。");
            var norm = 0d;
            for (var i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= tokenCount;
                norm += embedding[i] * embedding[i];
            }
            if (!double.IsFinite(norm) || norm <= 1e-20 || norm > float.MaxValue)
                throw new InvalidDataException("模型输出了零向量或超出数值范围的向量，无法执行语义搜索。");
            var scale = 1f / MathF.Sqrt((float)norm);
            for (var i = 0; i < embedding.Length; i++)
            {
                embedding[i] *= scale;
                if (!float.IsFinite(embedding[i]))
                    throw new InvalidDataException("模型输出了非有限向量，无法执行语义搜索。");
            }
            return embedding;
        }

        private static NamedOnnxValue CreateInput(string name, Type elementType, long[] values)
        {
            var dimensions = new[] { 1, values.Length };
            if (elementType == typeof(long))
                return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, dimensions));
            if (elementType == typeof(int))
                return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(values.Select(value => (int)value).ToArray(), dimensions));
            throw new InvalidDataException($"ONNX 输入 {name} 使用了不支持的类型 {elementType.Name}。");
        }

        public void Dispose() => _session.Dispose();
    }
}
