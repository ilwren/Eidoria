# Eidoria 端到端验收报告（真实权重）

- 生成时间：2026-11-06 01:54:07 +00:00
- 运行环境：Unix 7.0.0.1019 / X64 / .NET 10.0.14
- 模型目录：`/home/runner/work/_temp/mdexplorer-real-models`
- 模型 `text-embed-zh`：BGE small 中文嵌入 · revision `75c43b069aac4d136ba6bc1122f995fedcfd2781` · 512 维
- 模型 `text-embed-en`：MiniLM L6 v2 英文嵌入 · revision `751bff37182d3f1213fa05d7196b954e230abad9` · 384 维

## 链路段耗时

| 阶段 | 耗时 | 备注 |
|---|---|---|
| 文件名索引重建 | 37.4 ms | 12 项 |
| AI 向量索引重建 | 792.5 ms | 含 ONNX Session 加载 |
| 首次推理（模型加载） | 2.7 ms | 输出 512 维 |
| 查询中位延迟 | 5.2 ms | 16 条查询各跑 3 次取中位 |
| 查询 P90 | 6.1 ms | — |

## 质量

- Top-1 命中：**16/16**（100 %）
- Top-3 命中：**16/16**（100 %）
- MRR：**1.000**
- 命中模型分布：BGE small 中文嵌入 ×8、MiniLM L6 v2 英文嵌入 ×8

| # | 查询 | 期望 | 实际 Top-1 | 名次 | Top-1 分数 | 该文件分数 | 模型 | 中位延迟 |
|---|---|---|---|---|---|---|---|---|
| 1 | 忘记密码了怎么重置 | account-password.md | account-password.md | 1 | 0.663 | 0.663 | BGE small 中文嵌入 | 7.9 ms |
| 2 | 退货之后退款要等多久 | refund-policy.md | refund-policy.md | 1 | 0.642 | 0.642 | BGE small 中文嵌入 | 6.1 ms |
| 3 | 番茄和鸡蛋一起炒怎么做 | tomato-egg-recipe.md | tomato-egg-recipe.md | 1 | 0.609 | 0.609 | BGE small 中文嵌入 | 5.6 ms |
| 4 | 周六周日适合出门吗 | weekend-weather.md | weekend-weather.md | 1 | 0.539 | 0.539 | BGE small 中文嵌入 | 5.2 ms |
| 5 | 笔记本电池怎么能用久一点 | laptop-battery.md | laptop-battery.md | 1 | 0.594 | 0.594 | BGE small 中文嵌入 | 5.5 ms |
| 6 | 公司报销要开发票怎么办 | invoice-tax.md | invoice-tax.md | 1 | 0.544 | 0.544 | BGE small 中文嵌入 | 5.7 ms |
| 7 | 变基时冲突太多想放弃 | git-rebase.md | git-rebase.md | 1 | 0.512 | 0.512 | BGE small 中文嵌入 | 5.6 ms |
| 8 | 面包的酵种一直不发酵 | sourdough.md | sourdough.md | 1 | 0.626 | 0.626 | BGE small 中文嵌入 | 5.7 ms |
| 9 | reset a forgotten login password | account-password.md | account-password.md | 1 | 0.601 | 0.601 | MiniLM L6 v2 英文嵌入 | 1.8 ms |
| 10 | how many days until the money comes back after a return | refund-policy.md | refund-policy.md | 1 | 0.585 | 0.585 | MiniLM L6 v2 英文嵌入 | 1.4 ms |
| 11 | quick recipe with tomato and egg | tomato-egg-recipe.md | tomato-egg-recipe.md | 1 | 0.681 | 0.681 | MiniLM L6 v2 英文嵌入 | 1.0 ms |
| 12 | will it rain on the weekend | weekend-weather.md | weekend-weather.md | 1 | 0.487 | 0.487 | MiniLM L6 v2 英文嵌入 | 1.0 ms |
| 13 | make my laptop battery last longer | laptop-battery.md | laptop-battery.md | 1 | 0.527 | 0.527 | MiniLM L6 v2 英文嵌入 | 1.0 ms |
| 14 | need a company invoice with tax id | invoice-tax.md | invoice-tax.md | 1 | 0.652 | 0.652 | MiniLM L6 v2 英文嵌入 | 1.1 ms |
| 15 | too many conflicts during git rebase | git-rebase.md | git-rebase.md | 1 | 0.685 | 0.685 | MiniLM L6 v2 英文嵌入 | 1.2 ms |
| 16 | sourdough starter is not rising | sourdough.md | sourdough.md | 1 | 0.501 | 0.501 | MiniLM L6 v2 英文嵌入 | 1.1 ms |

## 怎么读这份报告

- **Top-1 / MRR** 是端到端质量：它包含模型、路由（中文走 BGE、英文走 MiniLM）、索引与排序的全部影响。
- **Top-1 分数与该文件分数** 是同一模型的余弦相似度；两者差距太小（< 0.05）说明这条查询本身区分度低，不是链路坏了。
- **延迟** 是 CPU 上的单次查询耗时，含一次查询编码；它不代表批量索引吞吐。
- 语料是 12 篇合成文档（含 2 篇纯干扰项），查询刻意不含目标文件名，避免退化成关键词匹配。
- 工作目录：`/tmp/mdexplorer-acceptance/b31e92076f834ba99b36177cdfc2d92f`（跑完即删，报告里保留路径便于对照）。
