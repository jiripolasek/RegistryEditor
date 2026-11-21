// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

#if REGEDIT_EXPERIMENTAL_TERMINAL
using EasyWindowsTerminalControl;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Services;
using Microsoft.Terminal.Wpf;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Views.Tools;

internal sealed partial class PowerShellView : UserControl, IDisposable
{
    private readonly TermPTY _session = new();
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false };
    private readonly ScrollViewer _headerScroll = new();
    private readonly Border _terminalSurface = new()
    {
        // Match the native renderer's default background in the scrollbar gutter.
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 12, 12)),
        RequestedTheme = ElementTheme.Dark
    };
    private readonly Func<FrameworkElement> _createRecoveryActions;
    private readonly bool _isScript;
    private TerminalControl? _terminal;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _sessionRunning;
    private bool _startupFailed;

    internal string ShellName { get; }

    internal string ShellDetails { get; }

    internal PowerShellView(
        string shellName, string shellDetails, string? commandLine, string? error, PowerShellRun? run,
        Func<FrameworkElement> createRecoveryActions)
    {
        this.ShellName = shellName;
        this.ShellDetails = shellDetails;
        this._createRecoveryActions = createRecoveryActions;
        this._isScript = run is not null;
        var layout = new Grid
        {
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }
        };
        this._headerScroll.Content = this._status;
        layout.Children.Add(this._headerScroll);
        this.Content = layout;
        this._status.Title = $"Starting {shellName}…";
        AutomationProperties.SetAutomationId(this._status, "PowerShellStatus");
        if (commandLine is null)
        {
            this.ShowFailure(error);
            return;
        }

        try
        {
            var terminal = new TerminalRenderer { AutoResize = true };
            this._terminal = terminal;
            AutomationProperties.SetName(terminal, $"{shellName} terminal");
            AutomationProperties.SetAutomationId(terminal, "PowerShellTerminal");
            terminal.Loaded += Start;
            this._terminalSurface.Child = terminal;
            Grid.SetRow(this._terminalSurface, 1);
            layout.Children.Add(this._terminalSurface);
        }
        catch (Exception ex)
        {
            this.ShowFailure(ex.Message);
        }

        async void Start(object sender, RoutedEventArgs args)
        {
            var terminal = (TerminalControl)sender;
            terminal.Loaded -= Start;
            if (this._disposed)
            {
                return;
            }

            var columns = Math.Max(1, terminal.Columns);
            var rows = Math.Max(1, terminal.Rows);
            var dispatcher = terminal.DispatcherQueue;
            this._session.TermReady += (_, _) =>
            {
                // Connect the renderer before releasing the output reader.
                if (!dispatcher.TryEnqueue(() => this.Connect(terminal)) ||
                    !this._connected.Task.GetAwaiter().GetResult())
                {
                    this._session.StopExternalTermOnly();
                }
            };

            this._sessionRunning = true;
            if (run is not null)
            {
                _ = this.ReportScriptResultAsync(run, this._lifetime.Token);
            }

            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        this._session.Start(commandLine!, columns, rows);
                    }
                    finally
                    {
                        try
                        {
                            this._session.CloseStdinToApp();
                        }
                        catch (ObjectDisposedException)
                        {
                            // TermPTY.Start owns the handles and may have closed the input pipe.
                        }
                    }
                });
                if (!this._disposed && !this._startupFailed && run is null)
                {
                    this.ShowStatus("PowerShell session ended", message: "Review any errors in the terminal below.");
                    this._status.Content = this._createRecoveryActions();
                }
            }
            catch (Exception ex)
            {
                if (!this._disposed)
                {
                    this.ShowFailure(ex.Message);
                }
            }
            finally
            {
                this._connected.TrySetResult(false);
                this._sessionRunning = false;
                if (this._disposed)
                {
                    this.ReleaseRenderer();
                }
            }
        }
    }

    private void Connect(TerminalControl terminal)
    {
        if (this._disposed)
        {
            return;
        }

        try
        {
            terminal.Connection = this._session;
            this._session.Win32DirectInputMode(true);
            this._status.IsOpen = false;
            this._headerScroll.Visibility = Visibility.Collapsed;
            this._connected.TrySetResult(true);
        }
        catch (Exception ex)
        {
            this._connected.TrySetResult(false);
            this.ShowFailure(ex.Message);
        }
    }

    internal void SetSurfaceVisible(bool visible)
    {
        if (this._terminal is { } terminal)
        {
            var visibility = visible && !this._startupFailed ? Visibility.Visible : Visibility.Collapsed;
            terminal.Visibility = visibility;
            this._terminalSurface.Visibility = visibility;
        }
    }

    internal void RefreshRecoveryActions()
    {
        if (this._status.Content is not null)
        {
            this._status.Content = this._createRecoveryActions();
        }
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this._lifetime.Cancel();
        this._lifetime.Dispose();
        this.SetSurfaceVisible(false);
        if (!this._sessionRunning)
        {
            this.ReleaseRenderer();
            return;
        }

        if (this._connected.TrySetResult(false))
        {
            return;
        }

        // Closing ConPTY can block while the output pipe drains.
        _ = Task.Run(() =>
        {
            try
            {
                if (this._session.Process is { HasExited: false } process)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // PowerShell can exit between checking HasExited and stopping it.
            }
        });
    }

    private void ReleaseRenderer()
    {
        if (this._terminal is { } terminal)
        {
            terminal.Connection = null;
            // shortcut: The beta exposes its HWND host via XAML, use its public disposal API when available.
            (terminal.FindName("termContainer") as IDisposable)?.Dispose();
            this._terminal = null;
        }
    }

    private async Task ReportScriptResultAsync(PowerShellRun run, CancellationToken token)
    {
        try
        {
            if (!await this._connected.Task.WaitAsync(token))
            {
                return;
            }

            this.ShowStatus("Running reviewed PowerShell script…");
            var result = await run.DeliverAsync(token);
            if (!this._disposed)
            {
                this.ShowResult(result);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!this._disposed)
            {
                this.ShowStatus("PowerShell could not report a script result", InfoBarSeverity.Error, error.Message);
            }
        }
    }

    private void ShowStatus(string title, InfoBarSeverity severity = InfoBarSeverity.Informational, string? message = null)
    {
        this._status.Title = title;
        this._status.Message = message ?? string.Empty;
        this._status.Severity = severity;
        this._status.IsOpen = true;
        this._headerScroll.Visibility = Visibility.Visible;
    }

    private void ShowResult(PowerShellRunResult result)
    {
        this.ShowStatus(
            result.Succeeded ? "PowerShell script completed" : "PowerShell script failed",
            result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            result.Succeeded ? null : "Review the error in PowerShell. The script may have applied some changes.");
        var copy = new Button { Content = "Copy result" };
        AutomationProperties.SetAutomationId(copy, "PowerShellCopyResult");
        copy.Click += (_, _) =>
        {
            var locations = string.Join(
                Environment.NewLine, result.Targets.Select(target => $"{target.Address} ({target.View})"));
            var text = $"{this._status.Title}{Environment.NewLine}{locations}";
            if (!string.IsNullOrEmpty(result.Error))
            {
                text += $"{Environment.NewLine}{result.Error}";
            }

            try
            {
                ClipboardText.Set(text);
                copy.Content = "Copied";
            }
            catch (Exception error)
            {
                this._status.Message = $"Could not copy the result. {error.Message}";
            }
        };
        this._status.ActionButton = copy;
    }

    private void ShowFailure(string? error)
    {
        var started = this._connected.Task.IsCompletedSuccessfully && this._connected.Task.Result;
        if (!started)
        {
            this._startupFailed = true;
            this.SetSurfaceVisible(false);
            Grid.SetRowSpan(this._headerScroll, 2);
        }

        this.ShowStatus(started ? "PowerShell session failed" : "PowerShell could not start", InfoBarSeverity.Error, error);
        if (!started || !this._isScript)
        {
            this._status.Content = this._createRecoveryActions();
        }
    }

    private sealed partial class TerminalRenderer : TerminalControl
    {
        // shortcut: The beta queries its own peer while creating it, remove after upstream fixes the recursion.
        protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    }
}
#endif
