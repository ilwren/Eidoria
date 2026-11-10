# Eidoria 插件与扩展系统路线（MEPX v1）

## 状态与范围

**已实现：P0 编译期内部提供者注册表；P1 本地 MEPX v1 声明式主题包首个纵切。** 本文后续章节是建议的产品/协议设计，不代表对应能力已经可用或已经批准排期。

当前发布仍是本地文件资源管理器：不支持 WebDAV、SMB、FTP、NFS、云盘或网络下载插件。MEPX v1 只含主题数据，不能声明动作、打开方式、节点任务、网络或文件系统访问。后续如果要增加远程文件系统，应通过明确的 Provider 契约接入，不应把“插件”理解为在主进程里动态加载 DLL。

## 已实现

### P0：编译期内部提供者

- 预览内容由 `IPreviewProvider` 产出，`PreviewProviderRegistry` 按优先级选择；控件和 XAML 仍由宿主负责。
- 提供者在组合根显式注册，没有运行时 DLL 发现或动态加载。

### P1 首片：本地 MEPX v1 Material 3 主题包

扩展中心可导入、扫描、应用与移除本地 `.mepx` 包。当前协议刻意只允许主题颜色，不代表外部动作、打开方式或任意界面已可用。

```json
{
  "manifestVersion": 1,
  "id": "community.calm-theme",
  "name": "Calm Surface",
  "version": "1.2.0",
  "publisher": "Eidoria Community",
  "description": "A calm Material 3 seed color.",
  "capabilities": ["theme"],
  "theme": { "seedColor": "#6750A4" }
}
```

MEPX v1 验收/安全规则：

- ZIP 仅允许根目录唯一的 `manifest.json`；不接受其他文件，包括脚本、程序集、资源或 XAML。
- 压缩包 ≤ 1 MiB；清单解压后 ≤ 32 KiB；UTF-8 严格解码、JSON 最大嵌套深度为 8；拒绝重复 JSON 字段、未知字段、未知能力、不合法 ID/版本或非 `#RRGGBB` 颜色。
- 包保存在应用数据目录 `plugins/`。导入只通过系统文件选择器从本地文件选取，不提供插件市场或网络下载。
- 安装确认显示名称、发布者、能力和 SHA-256；哈希仅标识所选包，不是签名或可信证明。UI 明确提示包未签名、只从可信来源安装。
- 安装后主题只调用宿主现有的 Material 3 种子色功能；移除包不会意外更改已应用的当前主题色。
- 无效/重复 ID 的包不会被静默挑选；不同包同 ID 通过再次确认后原子替换。

## 节点编辑器：可编程任务方向

节点编辑器的目标应是**用受限、可审阅的图描述文件处理计划**，而不是把脚本编辑器换成方框。节点描述输入/输出与配置，宿主负责校验、授权、预览和执行；首选声明式节点，拒绝默认执行任意代码。

### 当前节点能力（v0.21 第一切片）

支持普通线性链，也支持一个有界条件 DAG：`当前文件夹 → 顺序类型/名称筛选 → 可选“匹配数量条件” → 达到阈值时传输；未达阈值安全结束或连到通知`。传输分支可与未达标分支在单个通知节点合流。最多 16 个节点、20 条连线、1 个条件、1 个传输和 1 个通知；只处理当前本地文件夹顶层条目（最多 100,000），匹配文件总大小最多 20 GiB，未知文件大小时停止；禁止循环、嵌套分支、递归、任意目标路径、脚本、多个工作流或后台触发。实现证据与限制见 `DEVELOPMENT.md` 的自动化记录。

### 建议的任务节点库（尚未实现）

| 类别 | 建议节点 | 用途与边界 |
|---|---|---|
| 触发/来源 | 手动运行、当前选中项、当前文件夹、用户明确选择的文件夹/授权根 | 从显式作用域产生 `FileSet`；不得悄悄扩大到全盘。远程来源只有对应 Provider 已授权后才可选 |
| 过滤/判断 | 文件种类、扩展名、名称模式、隐藏状态、大小区间、修改时间区间、匹配数量、目标是否存在 | 返回类型化布尔值或筛选后的 `FileSet`。名称模式用有界 glob；若将来支持正则，必须有超时与输入长度上限 |
| 规划/转换 | 预览操作计划、按类型/日期建立子目录、确定性重命名模板、排序/分组、文件名规范化 | 只生成计划或新值；模板只允许列出的占位符和有限格式，不提供表达式求值、任意路径拼接或脚本 |
| 文件动作 | 复制、移动、重命名、创建目录、压缩/解压、计算校验和、写出清单 | 所有写入通过宿主任务队列；先展示来源/目标/冲突策略，默认不覆盖、不永久删除；摘要和进度回到任务中心 |
| 信息与输出 | 通知、结果摘要、导出 CSV/JSON 清单、停止并报告 | 只输出有界结果；不得把密码、凭据、文件内容或敏感路径写入默认日志 |
| 后续扩展 | 文件创建/修改触发、定时触发、内容分类/本地 AI 节点、Provider 连接器节点 | 需要独立权限、后台生命周期、资源预算和 Android 能力评估；本地 AI 节点必须端侧运行且模型由用户显式准备 |

### 推荐分期

1. **自动化安全增强**：多条用户命名工作流、手动/选中项/显式来源根、更多元数据过滤、重命名模板、计划预览与 dry-run。执行仍是线性或有界 DAG，不加入脚本节点。
2. **有界控制流（部分实现）**：当前代码仅实现匹配数量阈值的单条件 If/Else、唯一传输分支与可选通知合流，节点和文件枚举有硬上限；一般条件、错误分支、逐文件映射、总字节/耗时预算仍未实现。后续扩展需继续定义 DAG 语义、取消传播并禁止无限循环；逐文件处理只可作为隐式有界遍历，不开放任意 `while`/递归。
3. **Provider-aware 文件任务**：节点输入输出使用 Provider-neutral 的 `FileLocator`，允许经授权的本地/SAF/远程 Provider 进行传输。跨 Provider move 必须是“目标完整写入并校验成功后才删除来源”，并明确其不是原子事务。
4. **触发器与后台任务**：最后再评估文件监视/定时器。桌面与 Android 后台限制不同，Android 需明确 WorkManager/前台服务、电量、通知与重启恢复策略；不承诺进程退出后仍实时触发。

### 节点定义与执行规范（未来协议要求）

- 每个节点有稳定的 `pluginId` + `nodeTypeId`、节点定义版本、显示名/说明、本地化资源键、类型化输入输出端口、配置 schema 版本、所需 capability 和执行预算。`nodeTypeId` 不随 UI 文案改名。
- 数据类型先限制为 `FileSet`、单个 `FileRef`、`Text`、`Boolean`、有限数值/日期、`OperationPlan`、`OperationResult` 等明确定义类型；不以 `object`/任意 JSON 作为隐式类型系统。端口不兼容要在运行前报错。
- 图模型与 NodifyM/Avalonia 控件解耦。画布只编辑坐标、连线与配置；验证器和执行器以稳定 ID/版本读取图。更换编辑器不应迁移工作流数据。
- 工作流持久化 `workflowSchemaVersion`、插件 ID、节点 ID、节点配置版本。插件缺失/节点版本过旧时保留原配置并显示“无法执行”，不得静默删除或改写用户图。
- 执行先产生不可变计划（来源、目标、操作、覆盖策略、预估范围），再由宿主预览/确认，最后交任务中心执行。插件节点不能绕过任务队列直接写用户文件。
- 节点实现只能调用宿主授予的最小服务接口，不把整个 Shell、DI 容器或任意文件系统实现交给扩展。副作用操作由宿主审核；纯计算节点可采用确定性输入输出和取消令牌。
- 禁止 shell 命令拼接、PowerShell、任意脚本、动态程序集加载、任意反射构造、无界递归/循环、默认联网、后台隐式触发。Lua/Python 是否开放仍须独立安全决策；不能被伪装成普通节点绕过限制。
- MoonSharp 目前只有隔离的 AOT/Trim/Android spike（`spikes/LuaPluginRuntime`），不是已批准的插件能力，也未接入扩展中心或自动化节点；门槛、限制和验证状态见 [`LUA-PLUGIN-SPIKE.md`](LUA-PLUGIN-SPIKE.md)。
- “打开方式/启动外部应用”是独立宿主能力，不随 Provider 或一般节点自动开放。若将来批准，必须让用户选择程序、参数以数组/占位符传递、明确能力和逐次确认，不允许 shell 字符串拼接；当前不实现。

## 文件系统 Provider：WebDAV 等的目标架构（提案）

### 目标与现状边界

当前 `FileLocator` 是包裹 `Path` 与可选 `content://` `StorageUri` 的结构；`IFileSystemService`、`IFileOperationService` 的主要契约仍以 `string` 定位串和本地路径语义为主，Android SAF 是受 UI/平台授权对象约束的特例。**当前不能把它们描述为已支持通用远程文件系统。**

建议保留应用侧单一门面：浏览器、预览、搜索和任务仍依赖宿主 `IFileSystemService` / `IFileOperationService`；在门面之下新增明确注册的 `IFileSystemProvider`/provider adapter，由 `FileLocator` 的 scheme/Provider ID 路由到 Local、Android SAF 或未来远程 Provider。应用层不直接依赖 WebDAV、HTTP 客户端或插件实现。

### Provider 契约

每个 Provider 应有不可变的 `providerId`（反向域名形式，例如 `org.example.webdav`）、协议版本、可用平台、能力集合和连接配置类型。能力按操作分别声明，而不是一个 `CanWrite` 总开关：`Browse/List`、`Stat`、`ReadStream`、`CreateFile`、`CreateFolder`、`WriteStream`、`Rename`、`Delete`，以及可选的 `ServerSideCopy/Move`、`Watch`、`Search`、`Seek/RangeRead`、`Trash`。不支持的动作必须返回明确 Unsupported，不以假成功或空结果降级。

Provider 要遵守下列语义：

- 定位符是 Provider-neutral 的、不含密钥的 URI/值对象。建议把协议、连接 profile ID 和不透明 item key 分开；不得把用户名、密码、Bearer token、签名 URL 或查询凭据放入 `FileLocator`、最近记录、工作流或日志。路径段必须单独编码，拒绝 `..` 穿越与错误 scheme 路由。
- `FileItem.Length`、修改时间、可写状态等远程元数据可能未知，模型须保留“未知”，不能伪造为 0 或当前时间。大小写、排序、Unicode 名称规范化、最大文件名长度和隐含目录均由 Provider 如实说明；宿主不得套用本地磁盘假设。
- 读写只能用可取消的流式 API，有界缓冲与背压；禁止为了远程文件把完整文件默认放入内存。超时、离线、鉴权、限流、冲突、空间不足与服务器故障要映射为可翻译的 `FileError`/`OperationError`，保留可诊断但不泄密的服务器状态。
- Provider 必须报告是否支持条件写入/版本标记（ETag 等）、原子替换、原子重命名、服务端复制/移动和恢复能力。不存在的能力不能由 UI 假设。
- 所有改动继续经过任务队列、进度、取消、冲突确认、部分失败明细与可审计结果。覆盖必须由用户策略明确授权；远程版本在计划与执行间变化时应报冲突，不盲目覆盖。

### 复制/移动跨 Provider 规则

- 同一 Provider 是否可以服务器端复制/移动由能力协商决定；跨 Provider 一律由宿主以读流→写流串行/限流传输，不让源 Provider 直接写目标。
- 跨 Provider copy 完成后关闭并确认目标写流成功；在可行时比较长度或 SHA-256。校验失败保留来源、报告目标不完整并提供清理目标的确认选项。
- 跨 Provider move 是两个阶段：copy + 验证成功 + 再删除源。它不是原子操作；若删除源失败，结果必须报告“复制成功、来源仍保留”，不得汇报全成功。默认不提供自动补偿删除/回滚。
- 远程目录递归复制/移动必须设条目数、单文件大小、总字节数、时间与并发上限；遇到权限不足的子树要给部分结果，不静默跳过。

### WebDAV 专项约定（候选 Provider，不是当前功能）

- 首版建议只处理用户显式创建的服务器 profile 与用户选定的根，不扫描局域网、不自动发现服务器、不后台同步。支持范围先限定 RFC 4918 常规目录/文件操作；锁、版本控制、日历/群件扩展和厂商专有扩展后置。
- 默认要求 HTTPS 并正常验证证书/主机名；不提供“忽略所有证书错误”。若确需允许 HTTP，只能是用户逐连接确认并显示明文风险。禁止在跨主机重定向时转发认证信息；不能默默降级 TLS。
- 登录信息独立于连接地址与配置，后续必须先有平台 `ICredentialStore`（Windows/Linux 凭据库与 Android Keystore/系统安全存储的可行性 Spike）。凭据不进普通 JSON 设置、工作流、崩溃日志、诊断包或 URL；访问凭据仅按连接 profile 请求，并允许用户撤销。
- `PROPFIND` 列举、`GET` 读、`PUT` 写、`MKCOL` 建目录、`MOVE`/`DELETE` 等能力逐一映射，必须按服务器能力和响应处理；ETag/`If-Match`/`If-None-Match` 用于避免过期覆盖。服务器不支持条件操作时降低能力并提示风险，不能宣称原子写入。
- 处理 `401/403/404/409/412/423/429/5xx`、超时、TLS 失败与无效 XML；重试只对可重试且无副作用请求，遵守 `Retry-After`，写入中断后核验远端状态再决定重试。拒绝 XML 外部实体/DTD，限制单次目录响应体积与条目数。
- 先做依赖 Spike：核对许可证、Avalonia/.NET 10、Linux Native AOT、Android trim/AOT、TLS 与大文件流式上传、证书错误路径；未过门则使用最小 HTTP/WebDAV 实现或只支持桌面/不采用，不因功能成熟而降低 AOT/许可门槛。

## 插件运行模型与开发规范（建议）

### 三种运行形态要区分

1. **声明式数据包（P1，当前）**：MEPX v1 只有主题；宿主解释数据，不执行包内代码。
2. **内置/同仓 Provider（优先支持全平台）**：代码由应用编译进宿主，组合根显式注册，适用于必须在 Android 与 Native AOT 上提供的能力；依旧走平台无关契约与 conformance tests。若目标是三端 WebDAV，首个 WebDAV Provider 应优先作为 AOT/Trim 验证过的宿主实现发布，而不是只做桌面 Worker。
3. **桌面进程外 Worker（P2 候选）**：Windows/Linux 用户从本地文件显式安装/选择，主程序与 worker 经版本化 JSON-RPC over stdio 通信；承载第三方节点或 Provider 的可选桌面实现。worker 不可作为程序集加载，Android 商店版不得下载或启动用户提供的可执行代码。

进程外只是崩溃/协议隔离，不等于 OS 安全沙箱：同一用户权限下的 worker 可能仍能读写用户文件或访问网络。安装/首次执行必须显式确认发布者、二进制哈希、平台、能力和完整风险；在真正有平台沙箱前，只运行用户明确选择并信任的 worker，不用 capability 文案虚称其被强制隔离。对 Android 或不可信输入，优先同仓内置实现与最小权限宿主 API。

### Manifest、身份与版本

未来可执行扩展格式必须与 MEPX v1 分开版本化，不能给主题包偷偷增加 `entryPoint` 或执行字段。Manifest 至少声明：`manifestVersion`、反向域名 `id`、包版本、作者/许可、最低宿主版本、支持 RID/平台、capability、Provider/Node 类型及 schema 版本、入口形态（内置注册或 worker）。能力按“可列举/读取/写入/重命名/删除/网络连接/后台触发”等最小集合声明；每个连接 profile 再限制 host 与授权范围。

协议使用独立 `protocolVersion`，握手时交换宿主、worker 和能力清单；未知必需能力/字段、主版本不兼容、最低宿主版本不符时 fail-closed。标识符不可随显示名变更。工作流引用精确的 `pluginId/nodeTypeId/configVersion`；升级需显式迁移和可回退备份，扩展被卸载后仍保留不可执行节点以便用户恢复。

### Worker RPC 建议

- stdin/stdout 只用于版本化协议，stderr 仅用于脱敏诊断；每条消息有 request ID、长度上限、请求超时和取消语义。协议错误、worker 崩溃和超时映射为可见任务失败。
- 提供 `initialize/describe/health`、节点执行、Provider capability 描述、文件列表/属性、流打开/分块读写等有限方法；不提供任意主机命令、任意路径 API 或任意环境变量转储。
- 文件内容必须分块、有背压、可取消；不允许把大文件一次性 Base64/JSON 化。具体 side-channel/分块帧格式需先通过吞吐与崩溃恢复 Spike 固化，不能在插件各自实现中发明不同协议。
- 每次调用按已授权的 profile/locator 发放短生命周期上下文；协议 capability 是宿主的业务限制，不冒充 OS 沙箱。任何外部副作用都要返回结构化结果与可诊断错误。

### 安全、许可和质量门

- 当前本地导入规则延续；不建立在线市场，不在 Android 应用内下载可执行代码。未来是否签名、信任根、撤销和更新机制尚未决定，任何可执行扩展进入实现前都要有 ADR 与威胁模型。
- 代码包必须有可核对的许可证、依赖 SBOM、源码/构建来源与 SHA-256；哈希不等于签名。许可不兼容、来源不明或未声明依赖时拒绝发行。
- 网络能力默认关闭、按连接授权；遵守 TLS 验证、重定向规则、DNS 解析变化与超时/大小限制。用户显式输入的局域网 WebDAV 地址可以按 profile 授权访问，但扩展不能借此扫描其他内网地址或访问未授权主机；跨主机重定向不得继承凭据。
- 不写密码/令牌/文件内容到设置、工作流、日志或错误弹窗；诊断只记录脱敏后的 host/profile ID、错误类别、请求 ID 与服务器状态码。
- 每个 Provider 必须通过共同 conformance suite：根与父级、分页/截断、未知元数据、空/大流、取消、并发、同名冲突、条件写入/变更、断线/重试、部分失败、恶意路径、鉴权撤销、AOT/Trim 发布。Worker 还需协议模糊测试、超时/崩溃恢复和资源上限测试。
- 第三方依赖先核 Apache/MIT/BSD 等许可证、传递依赖、Native AOT/Android 兼容与体积，再决定采用。WebDAV 真实服务器矩阵（例如 Nextcloud、Apache mod_dav、支持范围有限的通用服务器）需作为发布验收，不能用单一 mock 冒充互操作。

## 推荐下一步（文档决策先于代码）

1. 维护者确认：网络文件系统可作为显式选择加入的未来能力；当前 release 仍不实现、不联网。
2. 先形成 ADR：`FileLocator` 从 `string`/SAF 特例演进为可版本化 Provider identity；密码库/连接 profile；跨 Provider copy/move 与非原子语义。
3. 单独评审节点编辑器分期与 typed value/plan 规范，避免将存储 I/O、任意脚本、循环和后台调度一次塞进图模型。
4. 做 WebDAV 依赖与协议 Spike（包括 Native AOT 和 Android）；之后再决定它作为内置 Provider、桌面 Worker，还是暂不支持。
5. **当前已做的代码仍仅限 MEPX v1 主题数据和扩展中心；本文新列出的节点、Provider、WebDAV、Worker 事项都还未实现。**

## 相关代码

- P0：`src/MdExplorer.UI/Preview/PreviewProviders.cs`
- MEPX v1 验证/安装：`src/MdExplorer.Infrastructure/Plugins/PluginPackageCatalog.cs`
- 扩展中心逻辑与 UI：`src/MdExplorer.UI/ViewModels/ExtensionCenterViewModel.cs`、`src/MdExplorer.UI/Views/ExtensionCenterView.axaml`
- 包格式安全回归：`tests/MdExplorer.Tests/PluginPackageCatalogTests.cs`
- 自动化定义/执行：`src/MdExplorer.Domain/Automation.cs`、`src/MdExplorer.Application/Automation.cs`、`src/MdExplorer.UI/ViewModels/AutomationViewModel.cs`
