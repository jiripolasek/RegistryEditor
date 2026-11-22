// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Infrastructure.Interop;
using JPSoftworks.Regedit.ViewModels.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace JPSoftworks.Regedit.Dialogs;

internal sealed class WindowDialogPresenter
{
    private readonly FrameworkElement _owner;
    private readonly Window _ownerWindow;
    private readonly Action _overlap;
    private readonly MainWindowViewModel _state;

    internal bool IsOpen => this._state.IsDialogOpen;

    internal WindowDialogPresenter(Window ownerWindow, FrameworkElement owner, MainWindowViewModel state, Action overlap)
    {
        this._ownerWindow = ownerWindow;
        this._owner = owner;
        this._overlap = overlap;
        this._state = state;
    }

    internal Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        return this.PresentAsync(() => this.ShowContentDialogAsync(dialog));
    }

    internal Task<ContentDialogResult> ShowCloseConfirmationAsync(ContentDialog dialog)
    {
        return this.PresentAsync(() => this.ShowContentDialogAsync(dialog), closeConfirmation: true);
    }

    internal Task<ContentDialogResult> ShowAsync(DialogLayout layout, double maxWidth = 548)
    {
        return this.PresentAsync(() => this.ShowLayoutAsync(layout, maxWidth));
    }

    internal Task<ContentDialogResult> ShowWindowAsync(DialogLayout layout, SizeInt32 initialSize)
    {
        return this.PresentAsync(() => this.ShowModalWindowAsync(layout, initialSize));
    }

    private async Task<ContentDialogResult> PresentAsync(Func<Task<ContentDialogResult>> show, bool closeConfirmation = false)
    {
        if (!this._state.TryBeginDialog(closeConfirmation))
        {
            this._overlap();
            return ContentDialogResult.None;
        }

        try
        {
            return await show();
        }
        finally
        {
            this._state.EndDialog();
        }
    }

    private async Task<ContentDialogResult> ShowContentDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = this._owner.XamlRoot;
        dialog.RequestedTheme = this._owner.ActualTheme;
        return await dialog.ShowAsync();
    }

    private async Task<ContentDialogResult> ShowLayoutAsync(DialogLayout layout, double maxWidth)
    {
        layout.Prepare(captioned: false);
        var dialog = new ContentDialog { Content = layout };
        AutomationProperties.SetName(dialog, layout.AccessibleTitle);
        dialog.Resources["ContentDialogPadding"] = new Thickness(0);
        dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        dialog.Resources["ContentDialogMaxWidth"] = maxWidth;
        var root = this._owner.XamlRoot;
        var result = ContentDialogResult.None;
        var completed = false;

        void UpdateSize(XamlRoot sender, XamlRootChangedEventArgs? args)
        {
            layout.MaxHeight = Math.Max(0, sender.Size.Height - 32);
        }

        void Complete(ContentDialogResult answer)
        {
            if (!completed)
            {
                completed = true;
                result = answer;
                dialog.Hide();
            }
        }

        UpdateSize(root, null);
        root.Changed += UpdateSize;
        layout.Completed += Complete;
        dialog.Opened += (_, _) => dialog.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!completed && layout.IsLoaded)
            {
                layout.FocusOnOpen();
            }
        });
        dialog.Closing += (_, args) => args.Cancel = !layout.CanDismiss && !completed;
        try
        {
            await this.ShowContentDialogAsync(dialog);
            return result;
        }
        finally
        {
            root.Changed -= UpdateSize;
            layout.Completed -= Complete;
            dialog.Content = null;
        }
    }

    private async Task<ContentDialogResult> ShowModalWindowAsync(DialogLayout layout, SizeInt32 initialSize)
    {
        layout.Prepare(captioned: true);
        layout.RequestedTheme = this._owner.ActualTheme;
        var window = new Window { Title = layout.WindowTitleBar.Title, Content = layout };
        var completion = new TaskCompletionSource<ContentDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = ContentDialogResult.None;
        var completed = false;
        var closed = false;

        void Complete(ContentDialogResult answer)
        {
            if (!completed && !closed)
            {
                completed = true;
                result = answer;
                window.Close();
            }
        }

        layout.Completed += Complete;
        window.Closed += (_, _) =>
        {
            closed = true;
            completion.TrySetResult(result);
        };
        try
        {
            var handle = WindowNative.GetWindowHandle(window);
            DialogWindowNative.SetOwner(handle, WindowNative.GetWindowHandle(this._ownerWindow));
            var appWindow = window.AppWindow;
            var presenter = OverlappedPresenter.CreateForDialog();
            presenter.IsModal = true;
            appWindow.SetPresenter(presenter);
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(layout.WindowTitleBar);
            appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            if (!layout.CanDismiss)
            {
                DialogWindowNative.DisableClose(handle);
            }

            appWindow.Closing += (_, args) => args.Cancel = !layout.CanDismiss && !completed;
            appWindow.TitleBar.PreferredTheme = layout.RequestedTheme switch
            {
                ElementTheme.Light => TitleBarTheme.Light,
                ElementTheme.Dark => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode
            };
            appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            this.PositionWindow(appWindow, initialSize);
            appWindow.Show();
            layout.FocusOnOpen();
            var answer = await completion.Task;
            if (this._owner.XamlRoot is not null)
            {
                this._ownerWindow.Activate();
            }

            return answer;
        }
        finally
        {
            layout.Completed -= Complete;
            if (!closed)
            {
                completed = true;
                window.Close();
            }
        }
    }

    private void PositionWindow(AppWindow window, SizeInt32 initialSize)
    {
        var owner = this._ownerWindow.AppWindow;
        var work = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = this._owner.XamlRoot.RasterizationScale;
        var width = Math.Min((int)(initialSize.Width * scale), work.Width);
        var height = Math.Min((int)(initialSize.Height * scale), work.Height);
        window.MoveAndResize(new RectInt32(
            Math.Clamp(owner.Position.X + (owner.Size.Width - width) / 2, work.X, work.X + work.Width - width),
            Math.Clamp(owner.Position.Y + (owner.Size.Height - height) / 2, work.Y, work.Y + work.Height - height),
            width,
            height));
    }
}
