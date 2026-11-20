// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace JPSoftworks.Regedit.Views.Shell;

/// <summary>Common appearance and close confirmation for secondary application windows.</summary>
public partial class SecondaryWindow : Window
{
    private bool _canClose;
    private Action<Exception> _closeFailed = null!;
    private bool _closing;

    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    protected SecondaryWindow()
    {
        this.AppWindow.Closing += this.Window_Closing;
    }

    protected void InitializeWindow(
        UIElement titleBar,
        AppWindow owner,
        ElementTheme theme,
        string backdrop,
        double scale,
        SizeInt32 initialSize,
        SizeInt32 minimumSize,
        Action<Exception> closeFailed)
    {
        this._closeFailed = closeFailed;
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(titleBar);
        this.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var work = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Nearest).WorkArea;
        // Move the hidden window onto the owner's monitor before applying its scaled size.
        this.AppWindow.Move(new PointInt32(work.X, work.Y));
        var width = Math.Min((int)(initialSize.Width * scale), work.Width);
        var height = Math.Min((int)(initialSize.Height * scale), work.Height);
        var position = owner.Position;
        var size = owner.Size;
        this.AppWindow.MoveAndResize(
            new RectInt32(
                Math.Clamp(position.X + (size.Width - width) / 2, work.X, work.X + work.Width - width),
                Math.Clamp(position.Y + (size.Height - height) / 2, work.Y, work.Y + work.Height - height), width, height));
        if (this.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(minimumSize.Width * scale), work.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(minimumSize.Height * scale), work.Height);
        }

        this.ApplyTheme(theme);
        this.ApplyBackdrop(backdrop);
    }

    public void ApplyTheme(ElementTheme theme)
    {
        ((FrameworkElement)this.Content).RequestedTheme = theme;
        this.AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode
        };
        this.AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        this.AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
    }

    public void ApplyBackdrop(string backdrop)
    {
        this.SystemBackdrop = backdrop == "Acrylic"
            ? new DesktopAcrylicBackdrop()
            : new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
    }

    protected virtual Task<bool> ConfirmCloseAsync()
    {
        return Task.FromResult(true);
    }

    internal async Task<bool> TryCloseAsync()
    {
        if (this._canClose)
        {
            return true;
        }

        if (this._closing)
        {
            return false;
        }

        this._closing = true;
        try
        {
            if (!await this.ConfirmCloseAsync())
            {
                this.Activate();
                return false;
            }

            this._canClose = true;
            this.Close();
            return true;
        }
        finally
        {
            this._closing = false;
        }
    }

    protected async void RequestClose()
    {
        try
        {
            await this.TryCloseAsync();
        }
        catch (Exception ex)
        {
            this._canClose = false;
            this._closeFailed(ex);
        }
    }

    private void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (this._canClose)
        {
            return;
        }

        args.Cancel = true;
        // Leave the native closing callback before potentially destroying the window.
        this.DispatcherQueue.TryEnqueue(this.RequestClose);
    }
}
