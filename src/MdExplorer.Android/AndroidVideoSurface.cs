using Android.Graphics;
using Android.Views;
using Avalonia.Android;
using Avalonia.Platform;
using MdExplorer.UI.Media;

namespace MdExplorer.Android;

/// <summary>
/// Android 原生视频画面：用 <see cref="TextureView"/> 而不是 SurfaceView。
/// <para>
/// 理由：SurfaceView 是独立窗口层，永远盖在 Avalonia 之上，圆角、卡片背景、对话框浮层都压不住它；
/// TextureView 参与普通视图合成，能被布局裁剪、能被 Avalonia 内容覆盖，视觉上才像“文件管理器里的一块预览”，
/// 而不是“整块换了张脸”。代价是它要求硬件加速（Avalonia 的 GL 后端满足）且比 SurfaceView 多一次拷贝。
/// </para>
/// <para>
/// TextureView 的交由系统管理：<c>OnSurfaceTextureAvailable</c> 给出 <see cref="SurfaceTexture"/>，
/// 我们包成 <see cref="Surface"/> 交给 MediaPlayer；<c>OnSurfaceTextureDestroyed</c> 时如实通知后端失去画面。
/// </para>
/// </summary>
internal sealed class AndroidVideoSurfaceAttachment : IMediaSurfaceAttachment
{
    private readonly TextureView _view;
    private Surface? _surface;
    private bool _disposed;

    public AndroidVideoSurfaceAttachment(IPlatformHandle parent)
    {
        // 注意命名空间：本文件在 MdExplorer.Android 下，直接写 Application 会解析到 MdExplorer.Application。
        var context = (parent as AndroidViewControlHandle)?.View?.Context
                      ?? global::Android.App.Application.Context;
        _view = new TextureView(context);
        _view.SurfaceTextureListener = new Listener(this);
        Handle = new AndroidViewControlHandle(_view);
    }

    public IPlatformHandle Handle { get; }

    public event EventHandler<object>? SurfaceReady;
    public event EventHandler? SurfaceLost;

    private void OnAvailable(SurfaceTexture texture)
    {
        if (_disposed)
            return;
        _surface?.Dispose();
        _surface = new Surface(texture);
        SurfaceReady?.Invoke(this, _surface);
    }

    private void OnDestroyed()
    {
        SurfaceLost?.Invoke(this, EventArgs.Empty);
        _surface?.Dispose();
        _surface = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _view.SurfaceTextureListener = null;
        _surface?.Dispose();
        _surface = null;
        _view.Dispose();
    }

    private sealed class Listener(AndroidVideoSurfaceAttachment owner) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(SurfaceTexture? surface, int width, int height)
        {
            if (surface is not null)
                owner.OnAvailable(surface);
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture? surface)
        {
            owner.OnDestroyed();
            // true 表示由 TextureView 释放 SurfaceTexture；我们只包了 Surface，没有额外持有底层纹理。
            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture? surface, int width, int height)
        {
        }

        public void OnSurfaceTextureUpdated(SurfaceTexture? surface)
        {
        }
    }
}
