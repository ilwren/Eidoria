# Eidoria 端到端验收报告（真实权重）

- 生成时间：2026-11-09 09:05:58 +00:00
- 运行环境：Unix 7.0.0.1019 / X64 / .NET 10.0.14
- 模型目录：`/home/runner/work/_temp/mdexplorer-real-models`
- 模型 `text-embed-zh`：BGE small 中文嵌入 · revision `75c43b069aac4d136ba6bc1122f995fedcfd2781` · 512 维
- 模型 `text-embed-en`：MiniLM L6 v2 英文嵌入 · revision `751bff37182d3f1213fa05d7196b954e230abad9` · 384 维
- 已就绪 `text-embed-zh`：6/6 个文件、23.4 MB
- 已就绪 `text-embed-en`：6/6 个文件、22.8 MB
- 权重指纹（`onnx/model_quantized.onnx`）：
  - `afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1  text-embed-en/onnx/model_quantized.onnx`
  - `15b717c382bcb518ba457b93ea6850ede7f4f1cd8937454aa06972366cd19bcc  text-embed-zh/onnx/model_quantized.onnx`

## 链路段耗时

| 阶段 | 耗时 | 备注 |
|---|---|---|
| 首次推理（冷加载 ONNX Session） | 321.6 ms | 输出 512 维 |
| 文件名索引重建 | 128.3 ms | 12 项、1 个根 |
| AI 向量索引重建 | 2111.8 ms | 已加载会话；12 项被向量化 |
| 查询中位延迟 | 11.7 ms | 16 条查询各跑 3 次取中位 |
| 查询 P90 | 24.1 ms | — |

## 质量

- Top-1 命中：**15/16**（94 %）
- Top-3 命中：**16/16**（100 %）
- MRR：**0.969**
- 命中模型分布：BGE small 中文嵌入 ×8、MiniLM L6 v2 英文嵌入 ×8

| # | 查询 | 期望 | 实际 Top-1 | 名次 | Top-1 分数 | 该文件分数 | 模型 | 中位延迟 |
|---|---|---|---|---|---|---|---|---|
| 1 | 忘记密码了怎么重置 | account-password.md | account-password.md | 1 | 0.294 | 0.294 | BGE small 中文嵌入 | 24.6 ms |
| 2 | 退货之后退款要等多久 | refund-policy.md | refund-policy.md | 1 | 0.280 | 0.280 | BGE small 中文嵌入 | 24.1 ms |
| 3 | 番茄和鸡蛋一起炒怎么做 | tomato-egg-recipe.md | tomato-egg-recipe.md | 1 | 0.265 | 0.265 | BGE small 中文嵌入 | 10.5 ms |
| 4 | 周六周日适合出门吗 | weekend-weather.md | weekend-weather.md | 1 | 0.235 | 0.235 | BGE small 中文嵌入 | 11.7 ms |
| 5 | 笔记本电池怎么能用久一点 | laptop-battery.md | laptop-battery.md | 1 | 0.271 | 0.271 | BGE small 中文嵌入 | 16.6 ms |
| 6 | 公司报销要开发票怎么办 | invoice-tax.md | invoice-tax.md | 1 | 0.237 | 0.237 | BGE small 中文嵌入 | 17.9 ms |
| 7 | 变基时冲突太多想放弃 | git-rebase.md | git-rebase.md | 1 | 0.218 | 0.218 | BGE small 中文嵌入 | 11.9 ms |
| 8 | 面包的酵种一直不发酵 | sourdough.md | sourdough.md | 1 | 0.272 | 0.272 | BGE small 中文嵌入 | 11.7 ms |
| 9 | reset a forgotten login password | account-password.md | account-password.md | 1 | 0.849 | 0.849 | MiniLM L6 v2 英文嵌入 | 12.2 ms |
| 10 | how many days until the money comes back after a return | refund-policy.md | weekend-weather.md | 2 | 0.780 | 0.257 | MiniLM L6 v2 英文嵌入 | 10.0 ms |
| 11 | quick recipe with tomato and egg | tomato-egg-recipe.md | tomato-egg-recipe.md | 1 | 0.954 | 0.954 | MiniLM L6 v2 英文嵌入 | 5.9 ms |
| 12 | will it rain on the weekend | weekend-weather.md | weekend-weather.md | 1 | 0.932 | 0.932 | MiniLM L6 v2 英文嵌入 | 9.9 ms |
| 13 | make my laptop battery last longer | laptop-battery.md | laptop-battery.md | 1 | 0.936 | 0.936 | MiniLM L6 v2 英文嵌入 | 6.4 ms |
| 14 | need a company invoice with tax id | invoice-tax.md | invoice-tax.md | 1 | 0.953 | 0.953 | MiniLM L6 v2 英文嵌入 | 6.3 ms |
| 15 | too many conflicts during git rebase | git-rebase.md | git-rebase.md | 1 | 0.952 | 0.952 | MiniLM L6 v2 英文嵌入 | 6.8 ms |
| 16 | sourdough starter is not rising | sourdough.md | sourdough.md | 1 | 1.000 | 1.000 | MiniLM L6 v2 英文嵌入 | 6.1 ms |

## 怎么读这份报告

- **Top-1 / MRR** 是端到端质量：它包含模型、路由（中文走 BGE、英文走 MiniLM）、索引与排序的全部影响。
- **Top-1 分数与该文件分数** 是同一模型的余弦相似度；两者差距太小（< 0.05）说明这条查询本身区分度低，不是链路坏了。
- **延迟** 是 CPU 上的单次查询耗时，含一次查询编码；它不代表批量索引吞吐。
- 语料是 12 篇合成文档（含 2 篇纯干扰项），查询刻意不含目标文件名，避免退化成关键词匹配。
- 工作目录：`/tmp/mdexplorer-acceptance/88b92e7bef0f4332b796fc30fac5e875`（跑完即删，报告里保留路径便于对照）。
