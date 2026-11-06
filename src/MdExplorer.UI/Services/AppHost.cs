using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.ViewModels;
using MdExplorer.Infrastructure.Ai;
using MdExplorer.Infrastructure.Plugins;

namespace MdExplorer.UI.Services;

/// <summary>组合根（开发文档 §4.3）：手工装配全部服务，宿主与截图工具共用同一入口。</summary>
public static class AppHost
{
    public sealed record AppServices(
        IFileSystemService FileSystem,
        IFileOperationService Operations,
        ITrashService Trash,
        IVolumeService Volumes,
        IKnownFoldersService KnownFolders,
        IArchiveService Archive,
        ISearchService Search,
        ISettingsStore SettingsStore,
        IFileLauncherService Launcher,
        PlatformCapabilities Capabilities,
        string DataDirectory,
        string StartFolder,
        IFileInspectorService Inspector,
        IFolderWatchService FolderWatch,
        DelegatingFileSystemService StorageFileSystem,
        IReadOnlyList<AuthorizedRoot> PersistedRoots,
        ISystemClipboardService SystemClipboard,
        IFileNameIndexService SearchIndex,
        ISemanticSearchService SemanticSearch,
        IStorageInsightsService StorageInsights,
        ITextFileService TextFiles,
        ISystemNotificationService SystemNotifications,
        IMediaPlaybackService MediaPlayback,
        IThumbnailCache Thumbnails,
        IContentIndexService ContentIndex,
        AiModelCatalog AiModels,
        IAiModelDownloader AiModelDownloads,
        PluginPackageCatalog Plugins);

    /// <summary>
    /// 平台内嵌播放后端的注入点（平台自带解码栈）。Android 宿主在 Activity 创建时注册
    /// MediaPlayer 实现；桌面在 Program.Main 里注册 Media Foundation / GStreamer。
    /// </summary>
    public static Func<IMediaPlaybackService>? MediaPlaybackFactory { get; set; }

    private const string DefaultMediaDiagnostic =
        "当前宿主没有注册内嵌解码后端（桌面端为 Media Foundation / GStreamer，Android 为 MediaPlayer）。可用系统应用打开。";

    /// <param name="dataDirectory">设置/收藏落点；为空时按平台默认（LocalApplicationData/MdExplorer）。</param>
    /// <param name="startFolder">初始文件夹；为空时桌面=用户主目录，Android=个人目录。</param>
    /// <param name="launcher">打开方式服务；为空时使用桌面进程拉起实现（Android 宿主运行期由 MainView 重绑为 Launcher 实现）。</param>
    public static AppServices Build(
        string? dataDirectory = null,
        string? startFolder = null,
        IFileLauncherService? launcher = null,
        ISystemClipboardService? systemClipboard = null,
        ISystemNotificationService? systemNotifications = null)
    {
        var dataDir = dataDirectory ?? DefaultDataDirectory();
        Directory.CreateDirectory(dataDir);

        // 双形态委托：本地路径转发本地实现；content:// 走 StorageProvider（Android SAF，视图挂载后注入）
        var fileSystem = new DelegatingFileSystemService(new LocalFileSystemService(), new StorageItemRegistry());
        var operations = new DelegatingFileOperationService(new FileOperationService(), fileSystem);
        var knownFolders = new KnownFoldersService();
        // Android 没有可统一浏览/还原的系统回收站：改用应用数据目录内的配额回收站，
        // 同一服务同时覆盖应用专属目录与 SAF 授权树；桌面继续走系统回收站。
        var store = SettingsStore.LoadOrDefault(dataDir);
        ITrashService trash = OperatingSystem.IsAndroid()
            ? new AppTrashService(
                dataDir, fileSystem,
                quotaBytes: store.Settings.AppTrashQuotaBytes,
                retentionDays: store.Settings.AppTrashRetentionDays)
            : new TrashService();
        var textFiles = new DelegatingTextFileService(new LocalTextFileService(), new StorageTextFileService(fileSystem));
        var searchIndex = new FileNameIndexService(dataDir, store);
        // 全文内容索引：默认关闭；建库时才会在应用数据目录里出现 content-index.db。
        var contentIndex = new SqliteContentIndex(Path.Combine(dataDir, "content-index.db"), textFiles);
        var search = new IndexedSearchService(searchIndex, new SearchService(), store);
        var aiModels = new AiModelCatalog(AiModelCatalog.ResolveDirectory(dataDir));
        // 内置模型下载器：用户不必再去跑 PowerShell/bash 脚本；源与镜像可在设置里切换。
        var aiModelDownloads = new AiModelDownloader(aiModels);
        var embeddings = new OnnxTextEmbeddingService(aiModels);
        var semanticSearch = new SemanticSearchService(dataDir, searchIndex, store, embeddings);

        var start = startFolder
                    ?? knownFolders.Get(KnownFolder.Home)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
            start = Path.GetPathRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;

        var capabilities = new PlatformCapabilities(
            trash.CanMoveToTrash,
            trash.CanBrowseTrash,
            OperatingSystem.IsAndroid(),
            OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            UsesAppTrash: trash is AppTrashService);

        // 内嵌媒体后端只用平台自带解码栈（v0.46 移除 libmpv 方案：实测系统栈更稳）。
        // 宿主没注册（无头测试、未接线的宿主）时仍是那个“明确自述无后端”的实现。
        var mediaPlayback = MediaPlaybackFactory?.Invoke()
            ?? new UnsupportedMediaPlaybackService(DefaultMediaDiagnostic);

        return new AppServices(
            fileSystem,
            operations,
            trash,
            new VolumeService(),
            knownFolders,
            new ArchiveService(),
            search,
            store,
            launcher ?? new ProcessFileLauncher(),
            capabilities,
            dataDir,
            start,
            new FileInspectorService(),
            new FolderWatchService(),
            fileSystem,
            AuthorizedRootsStore.Load(dataDir),
            systemClipboard ?? new AvaloniaSystemClipboardService(),
            searchIndex,
            semanticSearch,
            new StorageInsightsService(),
            textFiles,
            systemNotifications ?? new MdSystemNotificationServiceAdapter(),
            // 内嵌媒体后端（v0.46 起只有平台栈；没注册时是“明确自述无后端”的实现）。
            mediaPlayback,
            // 缩略图磁盘缓存：容量自我限额（默认 192 MB），坏图/无权限走负缓存，
            // 目录放在应用数据目录下，与设置/授权根同一处，方便用户整体清理。
            new FileThumbnailCache(Path.Combine(dataDir, "thumbnails")),
            contentIndex,
            // 端侧 AI 的模型目录：只扫描文件；ONNX Session 在用户调用本地语义搜索时才延迟建立。
            aiModels,
            aiModelDownloads,
            // 插件包只接受本机显式导入的 MEPX 声明式数据，不执行其中的任何代码。
            new PluginPackageCatalog(dataDir));
    }

    public static ShellViewModel CreateShell(AppServices services) => new(services);

    private static string DefaultDataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "MdExplorer");
    }
}
