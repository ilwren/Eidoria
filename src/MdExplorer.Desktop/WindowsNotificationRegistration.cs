#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using MdExplorer.UI.Services;

namespace MdExplorer.Desktop;

/// <summary>
/// 未打包 Windows 桌面应用的 Toast 身份注册。Windows 要求开始菜单快捷方式携带与
/// ToastNotificationManager 相同的 AppUserModelID；仅在进程里传 ID 不会完成注册。
/// 进程 ID 在创建窗口前设置，快捷方式只在用户首次真正发送通知时创建。
/// </summary>
internal static class WindowsNotificationRegistration
{
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkInterfaceId = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid AppUserModelIdFormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint ClsCtxInprocServer = 0x1;
    private const uint ShellChangeCreate = 0x00000002;
    private const uint ShellChangeUpdateItem = 0x00002000;
    private const uint ShellChangePathUnicode = 0x0005;
    private const uint ShellChangeFlush = 0x1000;

    public static void TrySetProcessAppUserModelId()
    {
        try
        {
            ThrowIfFailed(SetCurrentProcessExplicitAppUserModelID(
                MdSystemNotificationServiceAdapter.WindowsAppUserModelId));
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.TraceWarning("Windows AppUserModelID registration failed: {0}", ex);
        }
    }

    public static string RegisterShortcutForCurrentUser()
    {
        TrySetProcessAppUserModelId();

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("Windows 无法定位 Eidoria 可执行文件，不能注册通知快捷方式。");

        var executableName = Path.GetFileName(executablePath);
        if (executableName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("testhost.exe", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("devenv.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("请从已发布的 Eidoria 程序发送 Windows 通知；当前宿主进程不能注册为 Toast 应用。");
        }

        var programsPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programsPath))
            throw new InvalidOperationException("Windows 未提供当前用户的开始菜单程序文件夹。");

        Directory.CreateDirectory(programsPath);
        var shortcutPath = Path.Combine(programsPath, "Eidoria.lnk");
        var shortcutAlreadyExisted = File.Exists(shortcutPath);
        var shellLinkObject = CreateShellLink();

        try
        {
            var shellLink = (IShellLinkW)shellLinkObject;
            ThrowIfFailed(shellLink.SetPath(executablePath));
            ThrowIfFailed(shellLink.SetWorkingDirectory(
                Path.GetDirectoryName(executablePath) ?? programsPath));
            ThrowIfFailed(shellLink.SetDescription("Eidoria（万象屿）"));
            ThrowIfFailed(shellLink.SetIconLocation(executablePath, 0));

            var propertyStore = (IPropertyStore)shellLinkObject;
            var appIdKey = new PropertyKey(AppUserModelIdFormatId, 5);
            var appIdValue = PropVariant.FromString(
                MdSystemNotificationServiceAdapter.WindowsAppUserModelId);
            try
            {
                ThrowIfFailed(propertyStore.SetValue(ref appIdKey, ref appIdValue));
                ThrowIfFailed(propertyStore.Commit());
            }
            finally
            {
                appIdValue.Dispose();
            }

            ((IPersistFile)shellLinkObject).Save(shortcutPath, true);
        }
        finally
        {
            if (Marshal.IsComObject(shellLinkObject))
                Marshal.FinalReleaseComObject(shellLinkObject);
        }

        if (!File.Exists(shortcutPath))
            throw new IOException("Windows 未能保存 Eidoria 开始菜单快捷方式。");

        SHChangeNotify(
            shortcutAlreadyExisted ? ShellChangeUpdateItem : ShellChangeCreate,
            ShellChangePathUnicode | ShellChangeFlush,
            shortcutPath,
            IntPtr.Zero);
        return shortcutPath;
    }

    private static object CreateShellLink()
    {
        var classId = ShellLinkClassId;
        var interfaceId = ShellLinkInterfaceId;
        ThrowIfFailed(CoCreateInstance(
            ref classId,
            IntPtr.Zero,
            ClsCtxInprocServer,
            ref interfaceId,
            out var unknown));
        try
        {
            return Marshal.GetObjectForIUnknown(unknown);
        }
        finally
        {
            if (unknown != IntPtr.Zero)
                Marshal.Release(unknown);
        }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint classContext,
        ref Guid interfaceId,
        out IntPtr instance);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr item2);

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr itemIdList);
        [PreserveSig] int SetIDList(IntPtr itemIdList);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int showCommand);
        [PreserveSig] int SetShowCmd(int showCommand);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(2)] public ushort Reserved1;
        [FieldOffset(4)] public ushort Reserved2;
        [FieldOffset(6)] public ushort Reserved3;
        [FieldOffset(8)] public IntPtr PointerValue;

        public static PropVariant FromString(string value) => new()
        {
            VariantType = 31, // VT_LPWSTR
            PointerValue = Marshal.StringToCoTaskMemUni(value),
        };

        public void Dispose()
        {
            if (PointerValue == IntPtr.Zero)
                return;
            Marshal.FreeCoTaskMem(PointerValue);
            PointerValue = IntPtr.Zero;
        }
    }
}
#endif
