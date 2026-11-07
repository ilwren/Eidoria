# PDF 正文抽取：依赖决策（v0.57 已实施）

> 状态：**已实施（v0.57）**——维护者在 v0.56 收尾后拍板接入 `PdfPig`；实施范围严格照本文 §3 的边界，
> 只把文字层取进全文索引，预览链路一个字没动。
> 结论先写在这里：**若要做，用 Apache-2.0 的 PdfPig（`PdfPig` 包，稳定线 0.1.x），只做「取文本进全文索引」这一件事**，
> 不引入渲染、不做版面还原、不改 PDF 预览链路（预览仍是 v0.24 起的「OfficeViewer 直接控件优先 + PDFium 光栅兼容」）。

## 1. 为什么不能复用现有 PDF 依赖

| 现有依赖 | 许可 | 能力 | 结论 |
|---|---|---|---|
| OfficeViewer.Avalonia 的 `PdfViewer`（直接控件） | MIT | 只负责**呈现**，公开 API 没有文本层 | 拿不到正文 |
| PDFtoImage / PDFium 5.2.x | MIT + BSD-3 | **只光栅化**，没有文本提取/命中位置接口 | 拿不到正文 |
| PDFBox 经 IKVM 移植 | Apache-2.0 | 有 | 会把 JVM 语义与巨大运行时而带进来，AOT/Android 风险高，不选 |

也就是说：**渲染与取文本是两条独立的依赖**，想在「全文」里搜 PDF，必须再引入一个能读文本层的库。

## 2. 候选比较

| 方案 | 许可 | 依赖形态 | 中文支持 | 风险 |
|---|---|---|---|---|
| **PdfPig（`PdfPig`）** | **Apache-2.0** | 纯托管、无原生二进制（对 Android/AOT 友好） | 走字体 `ToUnicode` CMap，中文常见 PDF 可读 | 预 1.0，小版本可能改 API；需实测 trim/AOT 与包体积 |
| iText 7 / iTextSharp | **AGPL-3.0 或商业** | 纯托管 | 好 | **许可不可接受**（本仓 Apache-2.0） |
| PDFsharp | MIT | 纯托管 | 弱 | 以「生成」为主，文本抽取不是强项 |
| 自己解析内容流 + CMap | 无 | 无 | 取决于实现 | CID 字体/ToUnicode/加密/对象流全要自己写，工作量大且极易出错，不划算 |

PdfPig 是 Apache PDFBox 的 .NET 移植，NuGet 包 ID 为 `PdfPig`，稳定版 0.1.11（2025-07）、0.1.12（2025-11）、
0.1.13（2025-12）、0.1.14（2026-03）、0.1.15、**0.1.16（2026-08-22，本仓采用）**，另有更频繁的 `-alpha` 预发布。
**固定 `0.1.16` 并写进 `Directory.Packages.props`**，不跟随 alpha——该版集中修了一堆解析健壮性（递归防护、颜色空间钳制、
xref 暴力扫描、内联图像、token/资源缓存），正好是「畸形件不能拖垮索引」最需要的。

## 3. 若实施，按这个边界做

1. **只做索引，不做预览**：新 `PdfDocumentText.TryExtract(path)`，输出 `string?`，与 v0.47 的 `OfficeDocumentText`
   同形态；`SqliteContentIndex` 对 `.pdf` 走它，失败返回 `null` → 跳过该篇（不让一份坏 PDF 拖垮整次重建）。
2. **硬顶**：文件 ≤ 32 MB（与 Office 同档）、最多取 400 页、单篇输出 ≤ 40 万字符（与 Office 同口径）、
   单篇总耗时带超时（建议 15 s，超时就当失败）；加密/带密码的 PDF 直接跳过（现有密码解锁只服务预览，不进索引）。
3. **扫描件照实说**：没有文本层的 PDF 取不到字，索引里就不会有它——设置页与「全文」模式的说明文案要写清
   「可搜索文字版 PDF；扫描件需先 OCR，本版不做 OCR」。
4. **不碰渲染链路**：不把 PdfPig 接到预览上，避免「同一份 PDF 两个引擎给出两块不一样的内容」。
5. **体积与 AOT**：包约 11 MB（`PdfPig` 0.1.x），需重新量五硬目标里的「体积 ≤45 MB / APK ≤60 MB」；
   现有 linux-x64 Native AOT 与 Android trimming 两个 Job 必须继续绿，若出现 trim 警告要显式 `TrimmerRootAssembly`
   或改走「仅桌面启用」（那会让三端行为不一致，作为最后手段）。

## 3.1 v0.57 实际落地

| 项 | 落地 |
|---|---|
| 抽取实现 | `src/MdExplorer.Infrastructure/PdfDocumentText.cs`（`MaxFileBytes` 32 MB / `MaxPages` 400 / `MaxOutputChars` 400 000 / `Timeout` 15 s 公开为常量，测试直接引用） |
| 接入点 | `SqliteContentIndex.IsCandidate` 与 `ReadDocumentTextAsync` 共用 `IsDocumentExtension`（OOXML / OpenDocument / PDF 同档 32 MB），**重建与 v0.56 增量两条路径同时生效** |
| 取词方式 | 逐页 `page.GetWords()` 拼空格——英文靠 `unicode61` 正常分词，中文照旧走写入侧重叠二元组；个别页取词失败时退回整页 `page.Text` |
| 失败即跳过 | 加密（`PdfDocumentEncryptedException`）、损坏、无文本层扫描件、超时、超限 → 一律 `null`，「这篇没有正文」，整次重建照常完成 |
| 文案 | 设置页「全文内容索引」说明、搜索中心「全文」模式提示都写明「含文本型 PDF 与 Office 文档；扫描件需 OCR，本版不做」 |
| 测试 | `PdfDocumentTextTests`（正文可取 / 扩展名闸 / 扫描件 null / 坏件与缺件 null / 多页顺序）+ `ContentIndexTests`（PDF 能被搜到、坏 PDF 不影响重建） |
| 预览 | **零改动**：OfficeViewer 直接控件与 PDFtoImage 光栅照旧 |

## 4. 验收口径（实施时照抄）

- 单测：文本版 PDF 能取到正文并被 `SqliteContentIndex` 搜到；加密 PDF 返回 `null`；超大/超页数被拒；
  坏文件不抛（返回 `null`）；Office 抽取路径不回归。
- 性能：1 万篇混合目录（含若干 PDF）重建仍在 `PerformanceSmokeTests` 的上界内；单篇 PDF 抽取不阻塞界面（重建本就跑在任务中心）。
- 文档：`docs/DEVELOPMENT.md` §6.4 增补一段与 changelog 一行；许可章节补 `PdfPig`（Apache-2.0）。

## 5. 开放问题（v0.57 已答复）

- ~~是否接受为「搜 PDF 正文」新增一个约 11 MB、预 1.0 的第三方依赖？~~ **接受**（2026-11-07 维护者拍板 `pdfpig`）。
- ~~若 AOT 量测不达标，是否接受「只在桌面启用」的三端不一致？~~ **暂不启用降级**：v0.57 两个 Job（linux-x64 Native AOT、
  Android trimming）按原样跑；若后续某次量测不达标，把具体数字写进这里再让维护者决定，不得静默降级。
- 仍未做：PDF 内嵌附件的正文、版面/表格结构还原、命中高亮定位、OCR。
