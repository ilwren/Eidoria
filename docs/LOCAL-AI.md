# 本地 AI 接入计划与可行性评估

状态：**A0 模型分发已落地；A2 本地文本语义搜索已接线**（v0.38，2026-10-30）。真实下载权重后的检索质量/时延仍待设备验收。
范围：全部**本地推理**——无云端 API、无遥测、不发起任何网络请求。取模型是显式的、用户自己动手的一次性动作（`scripts/fetch-ai-models.*`），应用本体离线可用。插件侧通道见 `docs/PLUGIN-SYSTEM.md` §5。

> 这一版不吹能力：设置页只说「装没装、占多大、缺几个文件」，没有装模型时明确写「现在不可用」，不会出现按下去没反应的 AI 按钮。

## 1. 场景与顺序（图片/音频小模型评估）

| 场景 | 候选模型 | 体积 | 许可 / 语言 | 产品定位 |
|---|---|---:|---|---|
| **文本向量检索** | BGE small zh v1.5 + all-MiniLM-L6-v2 | 26 + 23 MB | MIT + Apache-2.0；中文/英文分开 | 当前默认轻量包；文件名/正文语义检索 |
| **图片语义检索** | SigLIP 2 图文双塔（图像/文本各一个编码器） | 约 100–200 MB（量化选择待核） | Apache-2.0；多语 | 对「找海边的落日」最合适，但它只产向量/相似度，不会回答「这图里几个人」 |
| **图片问答/描述/OCR 线索** | **SmolVLM-256M-Instruct，ONNX q4f16** | **≈194 MB，256M 参数** | Apache-2.0；模型卡标注 English，中文能力需实测 | 本次加为可选下载项；一套三段 ONNX（视觉编码器、词嵌入、解码器）+ tokenizer/processor，适合生成图片描述、图像问答与文档视觉线索；不是严格 OCR 引擎 |
| **音频语音转写（ASR）** | **Whisper tiny multilingual，ONNX q4f16** | **≈57 MB，39M 参数** | Apache-2.0（ONNX 导出仓库声明；上游 OpenAI 源代码 MIT）；多语含中文 | 本次加为可选下载项；对音频生成文字/字幕，做不到可靠说话人分离，也不是音乐理解模型 |
| **音频事件分类** | **YAMNet ONNX** | **≈16 MB，3.7M 参数** | Apache-2.0；AudioSet 521 类 | 本次加为可选下载项；给出「音乐/说话/犬吠/警笛」一类标签，不输出转写文字 |
| **更小的纯语音选项** | Moonshine tiny | ≈27M 参数 | 主要英语，模型许可按具体 checkpoint 再核 | 英语设备语音/指令识别可能更小；不适合本产品中文优先的默认项，先不进清单 |
| **本地对话（LLM）** | Qwen2.5-0.5B 级等 | ≥0.4–1.5 GB | 权重许可逐个核 | 体积/内存不合适，排最后、仅桌面实验 |

**结论：**「多模态」不必等于一个巨大的万能模型。文件管理器里拆成**文本嵌入（≈49 MB 默认包）+ 音频 ASR（≈57 MB）+ 图文 VLM（≈194 MB）**，可逐项选装、共享本地 ONNX Runtime。这样默认下载仍很小；识图问答可按需再加约 194 MB。若只想用「以图搜图/自然语言搜图」，SigLIP 双塔更省算力且检索更准，没必要用 VLM 生成长回答；若想「读懂图片、给一句描述或问它图里有什么」，选 SmolVLM。音频需求要分开看：Whisper=「说了什么」（语音转写），YAMNet=「听起来是什么」（环境/事件类别），二者加起来约 73 MB。

风险提醒：SmolVLM 官方 checkpoint 确实有 ONNX 导出，但它是三张 ONNX 图，C# 侧仍需 image processor、chat template、跨图缓存与生成循环；**下载清单已具备，运行时没有接线**。Whisper 的 ONNX 文件可下载，实际 ASR 还需要音频解码/重采样、log-Mel 特征、token 解码循环；也尚未接线。中文 VLM 质量、Android CPU 速度与实际峰值内存必须上机测，不能由模型参数数直接推断。

## 2. 运行时选型

| 运行时 | 许可 | AOT | 三平台 | 结论 |
|---|---|---|---|---|
| **ONNX Runtime**（`Microsoft.ML.OnnxRuntime`） | MIT | 使用 CPU 路径；本次接入后需由 AOT/Android CI 验证，真实模型首次运行仍需设备验收 | Windows/Linux x64、Android arm64 | **A2 已接入**，CPU 执行量化 BERT embedding |
| **Microsoft.ML.Tokenizers** | MIT | 纯托管，BERT WordPiece | 全平台 | **A2 已接入**，从模型随包 `vocab.txt` 构造分词器 |
| llama.cpp（`LLamaSharp`） | MIT | 需原生库，AOT 可用 | 三平台有 binding | 留给 A5（对话），且仅桌面 |
| sherpa-onnx | Apache-2.0 | 原生库，AOT 可用 | 三平台 | 备选 ASR/TTS；当前 ASR 清单选 ONNX Whisper tiny |
| 系统 TTS | 随系统 | — | Windows SAPI / Linux speech-dispatcher / Android TextToSpeech | A1，零依赖 |

## 3. 模型分发（v0.37 已落地）

模型几十 MB，**不进 git**，也不在安装包里；取模型是用户显式动作。

### 3.1 三方共用一份清单

`scripts/ai-models.manifest` 是唯一真源，纯文本、`|` 分隔——因为 bash 里没有 JSON 解析器，而脚本、PowerShell 与应用必须看到同一份定义。每个 HF 仓库 revision 固定为 40 位 commit SHA，不从浮动 `main` 拉取，避免下一次更新悄悄换模型。

```
model|<id>|<名称>|<用途>|<许可>|<许可链接>|<HF 仓库>|<固定 revision SHA>|<运行时>|<约体积 MB>|<默认下载 true/false>
file|<id>|<仓库内路径>
```

应用把它作为**嵌入资源**（`MdExplorer.Ai.Models.manifest`）编译进来，所以「脚本下的目录」与「程序找的目录」不会漂移；清单写错会在开发期就抛 `InvalidDataException`，而不是给用户一张残缺的表。

### 3.2 目录布局与校验

```
<模型目录>/<模型 id>/<仓库内路径>      例：…/models/text-embed-zh/onnx/model_quantized.onnx
<模型目录>/checksums.txt             首次下载后记录每个文件的 SHA-256
```

- 默认目录：`MDEXPLORER_MODELS` → 否则 `<应用数据目录>/models`（Linux `~/.local/share/MdExplorer/models`、Windows `%LOCALAPPDATA%\MdExplorer\models`）。应用与脚本用的是同一套优先级。
- 下载：先写入同目录 `.part`，`curl -C -` 断点续传、`-f` 让 4xx/5xx 直接失败；SHA-256 与大小验证通过后才原子改名，半截文件/错误页永远不会被应用看到；小于 1 KiB 的文件判为失败。`--force` 从头下；普通网络中断留下 `.part` 供续传。
- 校验：首次完整下载记录 SHA-256；之后每次运行按记录校验，坏文件重新下载；`--verify` 只校验不下载，缺 checksum 也会返回失败，不会把「没校验」报成通过。`--clean --only <id>` 只删除指定模型及其 checksum，不影响其它已安装模型。
- 镜像：设置 `HF_ENDPOINT=https://hf-mirror.com` 给下载脚本用；PowerShell 还可显式传 `-Mirror https://hf-mirror.com`。
- 默认调用只下文本嵌入包（≈49 MB）；可选 ASR/VLM **不自动下载**，需显式 `--only audio-asr-tiny` / `--only audio-classify-yamnet` / `--only image-vlm-smol`。`--all` 才下全部（约 316 MB）。PowerShell 对应 `-Only audio-asr-tiny` / `-Only audio-classify-yamnet` / `-Only image-vlm-smol` / `-All`。
- 命令：`scripts/fetch-ai-models.sh`（Linux/macOS/WSL，GNU 风格 `--list / --only / --all / --dir / --verify / --dry-run / --clean / --force`）、`scripts/fetch-ai-models.ps1`（Windows PowerShell 7，PowerShell 风格 `-List / -Only / -All / -Dir / -Verify / -DryRun / -Clean / -Force`）。

### 3.3 许可链

| 模型 | 权重许可 | 结论 |
|---|---|---|
| BAAI BGE small zh v1.5（`Xenova/bge-small-zh-v1.5` 的 ONNX 导出） | MIT | 可随 Apache-2.0 项目分发 |
| sentence-transformers all-MiniLM-L6-v2（`Xenova/all-MiniLM-L6-v2`） | Apache-2.0 | 同上 |
| Whisper tiny multilingual (`Xenova/whisper-tiny` ONNX 导出) | Apache-2.0（导出仓库模型卡；上游 OpenAI 源代码 MIT） | 按实际下载仓库的 Apache-2.0 元数据遵循，保留 upstream notices |
| YAMNet ONNX (`andrelgomes/yamnet-onnx`) | Apache-2.0（导出仓库） | 声音事件分类；模型权重 16 MB |
| SmolVLM-256M-Instruct ONNX | Apache-2.0（官方模型卡） | 多文件三图，包含视觉编码器与文本生成模型 |
| ONNX Runtime / Microsoft.ML.Tokenizers | MIT | Linux Native AOT 与 Android trim/AOT 发布通过 CI；Android APK 还会实检 ONNX ELF 依赖与 16 KiB 对齐 |

> 权重只在用户机器上下载，不进入本仓库；`models/` 已在 `.gitignore` 里。

## 4. 应用侧现状（v0.38：A2 第一片）

- `AiModelCatalog` 仍只做文件存在性/体积扫描；模型 Session 不在启动或设置页初始化。`OnnxTextEmbeddingService` 在用户显式建立 AI 索引/搜索时才按需加载 ONNX Session 与本地 `vocab.txt`。
- **模型路由**：中文查询优先 BGE small zh v1.5（512 维）；无中文字符时优先 MiniLM L6 v2（384 维）；某模型没安装则回退已安装的另一个。BERT 使用 `[CLS]`/`[SEP]`，动态序列长度最长 128，masked mean pooling + L2 normalize；BGE 查询加检索指令前缀。
- **搜索中心**新增「关键词 / AI 语义」模式和「建立 / 更新 AI 索引」。必须先配置并重建现有文件名索引、在设置中打开本地 AI、下载文本模型，再显式生成向量索引；索引文件变化、根集合变化或模型版本变化都会使 AI 索引过期并要求重建。更新时仍重新枚举当前文件并重新生成受限的嵌入文本指纹（指纹不包含或保存原始正文）；仅当路径、名称、类型、大小、修改时间、嵌入文本指纹及模型 revision 均匹配时复用已有量化向量。这样即使文件大小不变且时间戳被还原，实际参与 embedding 的文本变化仍会触发重嵌入。旧版 v1 快照可读取，但没有指纹的条目会在下一次重建时重新嵌入。
- 语义索引只覆盖用户加入的本地根，最多 20,000 项；保存路径/文件类型/大小/时间和每个可用文本模型的 int8 量化向量，不保存原始正文。文件名与父目录始终参与；仅对受支持的纯文本扩展名、≤2 MiB 文件最多读取前 4,000 字符作为摘要；不读 PDF、Office、压缩包、图片或 SAF。命中前再次核验文件时间/大小，stale 项不会被返回。`清除本地搜索索引` 同时清除文件名与 AI 向量快照。
- `AppSettings.EnableLocalAi` 默认关闭；关闭时保留本机 AI 向量文件但不运行推理/搜索。`MDEXPLORER_MODELS` 仍可覆盖模型目录。设置卡与搜索中心说明数据只在本机处理。
- **Android 原生库解析修正**：Microsoft.ML.OnnxRuntime 的桌面 Linux RID 原生库可能通过共享 `net10.0` 资产图混入 Android APK，并与 Android AAR 内的 Bionic 库发生重名冲突。Android app head 的窄作用域 MSBuild target 只移除 `linux-*` 的 native copy/publish 项；Linux/Windows 依赖不受影响。CI 会解包 arm64 APK，拒绝 glibc/Linux 依赖，并验证 ONNX ELF 的每个 `LOAD` 段均按 16 KiB 对齐。
- **验收边界**：CI 使用确定性的假 embedding 覆盖重建、排序、持久化、模型版本失效、清除、开关及无效向量拒绝；另有 opt-in 的真实权重 smoke tests（检查 BGE/MiniLM ONNX 加载、输出维度、有限值、单位归一化，并以多组中英文相关/无关文本对检查基本排序）。Linux Native AOT、Android trimming/AOT 发布及 APK 原生库检查均通过。GitHub Actions 曾以固定 revision 的真实权重跑通 CPU smoke test；检索基准、跨实现 embedding 数值对齐、完整 top-k 指标、Android 真机质量/性能仍待验收。
- **本地真实模型验证**（需 .NET 10 与可访问 Hugging Face 的网络）：`model_dir="$HOME/.local/share/MdExplorer/models"; scripts/fetch-ai-models.sh --dir "$model_dir" && scripts/fetch-ai-models.sh --dir "$model_dir" --verify && MDEXPLORER_MODELS="$model_dir" MDEXPLORER_RUN_REAL_MODEL_TESTS=1 dotnet test tests/MdExplorer.Tests -c Release --filter FullyQualifiedName~RealModelEmbeddingTests`。这会用固定 revision 下载默认 BGE + MiniLM 两个模型，并实际运行 ONNX CPU 推理；每个模型对多个场景检查相关文本的余弦相似度是否高于无关文本，作为基本语义 smoke check（不是完整 top-k 质量基准）。
- **GitHub Actions 真实模型验证与缓存**：主 `CI` workflow 的 `Real model AI search smoke test (cached)` job 随每次 push、PR 和 workflow_dispatch 运行，并执行真实权重 ONNX CPU embedding/语义排序测试。首次 cache miss 或缓存校验失败时，`scripts/fetch-real-models-with-fallback.sh` 依次尝试 Hugging Face 固定 revision、HF-compatible `hf-mirror.com` 固定 revision、ModelScope `Xenova/*` 镜像；ModelScope 两个仓库也固定到已验证的 revision，逐文件按 API 给出的 SHA-256/大小验证。成功后通过 `actions/cache@v4` 缓存模型目录，key 由模型 manifest 与 ModelScope 固定 revision downloader 的哈希生成；升级模型 revision 会自动切换缓存 key。后续命中缓存先运行 `fetch-ai-models.sh --verify`，校验通过就不联网下载。缓存被 GitHub 淘汰或校验不通过时 job 会自动重下，若三处均失败则明确失败，不会跳过 AI 测试报绿。此 job 验证 GitHub Actions 的 Linux CPU 路径；Android 真机仍需单独测。

## 5. 后续开发计划（A2 优先）

当前 A2 是可用的第一版，不代表检索质量与设备性能已完成验收。**近期只继续打磨端侧文本语义搜索**；图像、音频、对话模型不与 A2 混做。所有推理继续本地执行，不上传查询、路径、正文或向量。

### A2.1 检索正确性与真实模型校准（P0）

- **Tokenizer / ONNX 对齐**：使用固定版本的参考实现（Transformers.js 或 sentence-transformers）对同一组中文、英文、混合语句比较 tokenizer IDs 与归一化 embedding；覆盖大小写、全半角、标点、换行、CJK 分词、未知词、空白和超长输入。确认 `[CLS]/[SEP]`、attention mask、pooling、归一化、输入类型、BGE 查询指令前缀及模型 revision 全部一致；阈值按模型分别测，不沿用一个未经验证的常数。
- **建立可复现的小型检索基准集**：用本地生成的文件名和文本样例覆盖中文/英文/混合语言、短文本、相似主题、不同主题、关键词不重合的语义同义表达、长文本与仅文件名可检索的项目；样例不得包含个人文件或真实用户路径。记录每个查询的相关文件及理由。
- **端到端指标**：每个模型分别报告 Recall@5、MRR@10、nDCG@10、无结果率，以及冷/热查询延迟；保留当前真实权重 smoke test，并增加检索层的固定回归案例。先测出基线再定质量门槛，避免凭主观调整 `MinimumScore`。
- **验收**：CPU 上实跑固定 revision 的 BGE 与 MiniLM；参考 embedding 差异在选定容差内；基准集报告可重复；相关文本排序和空结果行为有回归保护。模型缺失、损坏、输出非有限值/维数错误或 Session 初始化失败都应明确失败，不得静默退化成假结果。

### A2.2 长文本覆盖与文件类型边界（P1）

- 当前每个文件只存一个向量，文本读取最多 4,000 字符，但 BERT 输入最长 128 tokens，因此模型实际只能看到输入开头的一小段，不能把「读取字符数」误当成「有效语义覆盖量」。基于 A2.1 基准对比：扩大 token 上限、首/中/尾片段，或受限的多 chunk 向量；评估召回收益、索引体积、建索引耗时及 Android 内存后再选方案。
- 如采用 chunking，明确每文件最大 chunk 数、重叠策略、超长文件截断方式、单文件结果去重与聚合（取最高分或其他经基准验证的方法），并将索引体积/时间上限纳入容量计算。不得无限增加每文件向量导致超过 96 MiB 索引上限。
- 增补 UTF-8/BOM、异常编码、空文件、二进制伪装文本、超出 2 MiB、不可读文件及读取期间被修改等测试。先维持纯文本白名单；PDF/Office 抽取继续分期处理，不在本步骤悄悄扩大文件解析范围。
- **验收**：长文关键内容出现在开头/中段/末尾时，基准集能说明召回变化；文件大小、chunk 数、索引大小均有硬上限和清晰的部分索引状态。

### A2.3 排序、关键词与阈值（P1）

- 建立语义排序、现有文件名关键词搜索、混合排序三组对照；检查精确文件名、父文件夹名、术语/型号、拼写片段以及语义改写查询。语义向量不应取代用户对精确名称的预期。
- 若基准证明有收益，再实现混合排序（例如 Reciprocal Rank Fusion 或经过校准的分数融合），按模型/查询类型验证权重；避免直接把不可比的关键词分数与 cosine 分数相加。明确搜索模式标签、结果分数含义以及过滤条件。
- 评估固定 `MinimumScore` 是否造成短查询/混合语言的漏召回；优先用基准曲线和无结果率选值，必要时按模型分别配置。查询为空、纯标点、单词、自然语言句子和不同 `maxResults` 都应覆盖。
- **验收**：对照基准公开各方案 top-k 指标及性能；若无统计收益则保持现状，不为“混合”而增加复杂度。

### A2.4 增量索引、并发与快照兼容（P0）

- 已加入重建时的嵌入文本 SHA-256 指纹，目标是元数据未变但参与 embedding 的文本已变时仍重嵌入；指纹不是原文。补足以下回归：同大小且恢复时间戳的编辑、仅文件名/父目录变化、内容截断范围之外的编辑、模型集合/顺序/revision/维数变化、文件增删、符号链接、权限拒绝、重建取消与重建期间文件变化。
- 明确指纹语义：它只对应实际构造并传给 embedder 的有界文本，不代表整个文件的完整性校验；被读取上限截掉的尾部内容当前不影响向量。若未来改用 chunking，应同步更新指纹及快照格式。
- 验证 v1 快照可读取、无指纹条目只在下次重建时失去复用资格、v2 写入可原子替换；损坏/截断/超大快照应安全拒绝且不覆盖旧的有效索引。重建取消不得留下“当前”状态或半成品。
- **验收**：相同输入重建不调用模型；只对实际变更项重嵌入；取消/异常后索引状态可解释且可再次重建；Windows/Linux 文件系统时间精度差异下测试稳定。CI 的 push 与 PR 两种触发路径都需检查单测结果一致，出现“测试摘要全通过但 job 失败”时先查明日志/退出码，不据单一汇总判定通过。

### A2.5 性能、内存与容量（P0）

- 以 100、1,000、10,000（以及上限附近的 20,000）项测试纯文件名、短文本、最大受支持文本和两个模型均已安装的情形；分别记录首次建索引、增量重建、查询冷启动/热运行的 p50/p95 耗时、CPU 与峰值内存。
- Android arm64 真机优先验收：模型首次加载峰值、持续搜索峰值、取消响应、低内存设备行为、进后台/恢复及模型 Session 生命周期；同时验证 Native AOT / trimming 发布。不以 Linux CI 成功代替移动端性能结论。
- 检查 20,000 项与 96 MiB 索引限制、量化向量大小、候选扫描耗时、模型双加载内存和异常时临时文件清理。只有实测后再决定是否开放可调索引上限、模型选择或设备性能档位。
- **验收**：公布桌面与 Android 测量环境及结果；性能目标先在基准测量后制定。取消、过期索引和超限错误要有明确 UI 状态，不能卡住界面或静默返回部分结果。

### A2.6 搜索体验、可解释性与隐私（P1）

- 检查索引建立/更新/取消/过期/部分索引/模型缺失/损坏等状态在设置页和搜索中心是否一致；进度、已完成数量、是否复用向量和耗时应准确，失败后可继续使用关键词搜索。
- 评估是否在命中后按需重新读取本地文件并提供短摘录或命中字段；若做，须限制读取量、转义显示、不得把摘录写入索引或日志，并在文件已经变化时不展示旧内容。默认仍只存路径、元数据、指纹和量化向量。
- 确认清除索引会删除当前及临时快照；关闭本地 AI 时不执行推理；应用运行时不联网、不记录查询/文件正文/路径遥测。文档说明本地指纹是内容派生值，不是正文加密或防泄漏机制；应用数据目录权限按平台默认保护。
- **验收**：隐私说明与实际持久化字段一致；用户能看懂结果来自哪种搜索、索引是否完整，以及为什么 AI 搜索不可用。

### 后续能力（不阻塞 A2）

- **A3 图像搜索/标签**：先评估图文双塔 SigLIP（比 VLM 生成文本轻且更适合相似度检索），再考虑 YAMNet 音频分类标签与媒体元数据。与文本向量分开管理索引/结果类型；不写文件、不改 EXIF；先核验实际模型运行时、许可、AOT 和 Android 内存。
- **A4 音频转写**：接 Whisper tiny q4f16：平台解码音频为单声道 16 kHz → log-Mel → greedy decoder；限制文件大小/时长，支持取消与进度；导出 SRT 前先评估用户需求。容器不支持时交给系统应用打开。
- **A5 图像问答（可选）**：SmolVLM-256M q4f16 需要视觉 encoder / embed_tokens / decoder 三个 ONNX session、processor resize/patch 切图与生成循环；先测峰值内存、速度及中文效果，再决定 Android 是否开放。

## 6. 隐私与安全边界

- **无网络**：应用运行期不发起任何 HTTP 请求；模型由用户手动下载。没有「首次启动偷偷下模型」。
- **无遥测**：不上报查询、路径或统计。
- **数据不出本机**：索引与向量都在应用数据目录；用户可整目录删除。
- **模型文件可信边界**：ONNX 模型是数据而非代码，但仍按「用户自己下载的外部文件」对待——只从清单里的路径加载，路径不允许逃出模型目录。

## 7. 风险与未验证项

- **没有实际设备推理证据**：CI 已验证 Linux Native AOT、Android trimming/AOT 发布，以及 APK 内是 Bionic ONNX 库且 16 KiB 页面对齐；仍未在真机上加载模型、执行推理或测性能。
- **模型仓库路径可能漂移**：清单里的 HF 路径写的是 `Xenova/*` 镜像仓库；脚本失败时会打印完整 URL 与镜像提示，改清单即可，不需要重编译（应用读的是编译进去的同一份文件，改完重编一次）。
- **没有真机推理证据**：本仓还没有在设备上使用下载权重执行真实推理，因此没有搜索质量或性能数字；验收需补「100/1000 个文件索引耗时、单次查询延迟与 Android 峰值内存」。
- **int8 量化的精度损失**未评估：先按官方量化版发布，检索质量不达标再换 fp32（体积 ×4）。

## 8. 参考

- BGE small zh v1.5 模型卡与许可：<https://huggingface.co/BAAI/bge-small-zh-v1.5>
- all-MiniLM-L6-v2 模型卡：<https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2>
- SmolVLM-256M 模型卡、ONNX 三图示例、Apache-2.0 与文件体积：<https://huggingface.co/HuggingFaceTB/SmolVLM-256M-Instruct>
- Whisper tiny multilingual 模型卡/39M 参数与多语言说明：<https://huggingface.co/openai/whisper-tiny>
- Whisper ONNX 导出仓库模型卡（license: Apache-2.0）：<https://huggingface.co/Xenova/whisper-tiny>
- Whisper 上游 OpenAI LICENSE（MIT）：<https://github.com/openai/whisper/blob/main/LICENSE>
- YAMNet ONNX (AudioSet 521 类，16 MB) 模型卡/Apache-2.0：<https://huggingface.co/andrelgomes/yamnet-onnx>
- Hugging Face Hub model tree API（ONNX q4f16 文件大小快照）：<https://huggingface.co/api/models/HuggingFaceTB/SmolVLM-256M-Instruct/tree/main/onnx?expand=false> / <https://huggingface.co/api/models/Xenova/whisper-tiny/tree/main?recursive=true&expand=false>
- ONNX Runtime 许可与 Native AOT 说明：<https://github.com/microsoft/onnxruntime>
- .NET Android 中 ONNX Linux RID 与 Android AAR 冲突及 workaround：<https://github.com/microsoft/onnxruntime/issues/29270>
- Android 16 KiB 页面对齐要求：<https://developer.android.com/guide/practices/page-sizes>
- Microsoft.ML.Tokenizers：<https://github.com/dotnet/machinelearning>
