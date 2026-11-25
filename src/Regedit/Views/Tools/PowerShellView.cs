// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
#if !REGEDIT_SYSTEM_CONPTY
using EasyWindowsTerminalControl;
#endif
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.Views.Shell;
using Microsoft.Terminal.Wpf;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Views.Tools;

internal sealed partial class PowerShellView : UserControl, IDisposable, IPanelView
{
#if REGEDIT_SYSTEM_CONPTY
    private readonly SystemConPtySession _session = new();
#else
    private readonly TermPTY _session = new();
#endif
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false };
    private readonly ScrollViewer _headerScroll = new();
    private readonly Border _terminalSurface = new()
    {
        // Match the native renderer's default background in the scrollbar gutter.
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 12, 12)),
        RequestedTheme = ElementTheme.Dark
    };

    // TerminalSetTheme requires the complete native theme when changing the font.
    private static readonly TerminalTheme DefaultTheme = new()
    {
        DefaultBackground = 0x0C0C0C,
        DefaultForeground = 0xCCCCCC,
        DefaultSelectionBackground = uint.MaxValue,
        CursorStyle = CursorStyle.BlinkingBlockDefault,
        ColorTable =
        [
            0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1, 0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
            0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9, 0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2
        ]
    };

    private readonly Func<FrameworkElement> _createRecoveryActions;
    private readonly Func<PowerShellView, RegistryLocation, bool, Task> _navigateFromPowerShell;
    private readonly PowerShellLocationFeed? _locationFeed;
    private readonly ToolCommand[] _registryCommands = [];
    private readonly ToolCommand? _followPowerShell;
    private readonly AsyncRelayCommand? _showPowerShellKey;
    private readonly AsyncRelayCommand? _showBrowserKey;
    private readonly bool _isScript;
    private TerminalControl? _terminal;
    private RegistryLocation? _registryLocation;
    private RegistryLocation? _browserLocation;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _sessionRunning;
    private bool _startupFailed;

    internal string ShellName { get; }

    internal string ShellDetails { get; }

    string IPanelView.PanelCommandsId => "PowerShellActions";
    IReadOnlyList<ToolCommand> IPanelView.PanelCommands => this._registryCommands;
    PanelSearch? IPanelView.PanelSearch => null;
    void IPanelView.SetDocked(bool docked) { }

    internal PowerShellView(
        string shellName, string shellDetails, string? commandLine, string? error, PowerShellRun? run,
        string fontFamily, int fontSize,
        PowerShellLocationFeed? locationFeed, Func<PowerShellView, RegistryLocation, bool, Task> navigateFromPowerShell,
        Func<RegistryLocation?, Task> showKeyInPowerShell, Func<FrameworkElement> createRecoveryActions)
    {
        this.ShellName = shellName;
        this.ShellDetails = shellDetails;
        this._createRecoveryActions = createRecoveryActions;
        this._navigateFromPowerShell = navigateFromPowerShell;
        this._locationFeed = locationFeed;
        this._isScript = run is not null;
        if (locationFeed is not null)
        {
            this._followPowerShell = new ToolCommand("Follow PowerShell", Symbol.Link)
            {
                AutomationId = "FollowPowerShell",
                ToolTip = "Follow the registry location in this PowerShell tab.",
                IsToggle = true,
                IsEnabled = false
            };
            this._followPowerShell.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ToolCommand.IsChecked))
                {
                    _ = this.FollowCurrentRegistryLocationAsync();
                }
            };
            this._showPowerShellKey = new AsyncRelayCommand(
                () => this._registryLocation is { } location
                    ? this._navigateFromPowerShell(this, location, true)
                    : Task.CompletedTask,
                () => this._registryLocation is not null);
            this._showBrowserKey = new AsyncRelayCommand(
                () => showKeyInPowerShell(this._browserLocation),
                () => this.CanSetRegistryLocation(this._browserLocation));
            this._registryCommands =
            [
                this._followPowerShell,
                new ToolCommand("Show key in browser", Symbol.Up)
                {
                    AutomationId = "ShowPowerShellKeyInBrowser",
                    ToolTip = "Open the current PowerShell registry key in the browser.",
                    Command = this._showPowerShellKey
                },
                new ToolCommand("Show browser key in PowerShell", Symbol.Sync)
                {
                    IconSource = new FontIconSource { Glyph = "\uE74B", FontSize = 16 },
                    AutomationId = "ShowBrowserKeyInPowerShell",
                    ToolTip = "Set PowerShell's registry location at the next empty prompt.",
                    Command = this._showBrowserKey
                }
            ];
        }

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

            var dispatcher = terminal.DispatcherQueue;
            // Connect the renderer before releasing the output reader.
            bool ConnectAndWait() => dispatcher.TryEnqueue(() => this.Connect(terminal)) &&
                this._connected.Task.GetAwaiter().GetResult();
#if !REGEDIT_SYSTEM_CONPTY
            this._session.TermReady += (_, _) =>
            {
                if (!ConnectAndWait())
                {
                    this._session.StopExternalTermOnly();
                }
            };
#endif

            this._sessionRunning = true;
            var cancellationToken = this._lifetime.Token;
            using var locationLifetime = locationFeed is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var reportLocations = locationFeed is not null && locationLifetime is not null
                ? this.WatchRegistryLocationsAsync(locationFeed, locationLifetime.Token)
                : Task.CompletedTask;

            if (run is not null)
            {
                _ = this.ReportScriptResultAsync(run, cancellationToken);
            }

            try
            {
                if (fontFamily != AppSettings.DefaultTerminalFontFamily || fontSize != AppSettings.DefaultTerminalFontSize)
                {
                    terminal.SetTheme(DefaultTheme, fontFamily, (short)fontSize);
                }

                var columns = Math.Max(1, terminal.Columns);
                var rows = Math.Max(1, terminal.Rows);
#if REGEDIT_SYSTEM_CONPTY
                await Task.Run(() => this._session.RunAsync(commandLine!, columns, rows, ConnectAndWait, cancellationToken));
#else
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
#endif
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
                locationLifetime?.Cancel();
                await reportLocations;
                this._connected.TrySetResult(false);
                this._sessionRunning = false;
                if (this._disposed)
                {
                    this.ReleaseRenderer();
                }
            }
        }
    }

    internal Task FollowCurrentRegistryLocationAsync() =>
        this._followPowerShell is { IsChecked: true, IsEnabled: true } &&
        this._locationFeed is { IsLocationChangePending: false } && this._registryLocation is { } location
            ? this._navigateFromPowerShell(this, location, false)
            : Task.CompletedTask;

    internal void SetBrowserLocation(RegistryLocation? location)
    {
        this._browserLocation = location;
        this._showBrowserKey?.NotifyCanExecuteChanged();
    }

    internal bool CanSetRegistryLocation(RegistryLocation? location) =>
        !this._disposed && this._locationFeed?.CanSetLocation(location) == true;

    internal async Task<string?> SetRegistryLocationAsync(RegistryLocation location)
    {
        var cancellationToken = this._lifetime.Token;
        try
        {
            var request = this._locationFeed!.SetLocationAsync(location, cancellationToken);
            this._showBrowserKey?.NotifyCanExecuteChanged();
            return await request;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            this._showBrowserKey?.NotifyCanExecuteChanged();
        }
    }

    private async Task WatchRegistryLocationsAsync(PowerShellLocationFeed feed, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var location in feed.WatchAsync(cancellationToken))
            {
                if (this._disposed)
                {
                    return;
                }

                var locationChanged = location is not null && !location.IsSameLocation(this._registryLocation);
                this._registryLocation = location;
                this._followPowerShell!.IsEnabled = true;
                this._showPowerShellKey!.NotifyCanExecuteChanged();
                this._showBrowserKey!.NotifyCanExecuteChanged();
                if (locationChanged)
                {
                    _ = this.FollowCurrentRegistryLocationAsync();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep PowerShell usable if its location pipe cannot connect or disconnects.
        }
        finally
        {
            this._registryLocation = null;
            this._followPowerShell!.IsEnabled = false;
            this._showPowerShellKey!.NotifyCanExecuteChanged();
            this._showBrowserKey!.NotifyCanExecuteChanged();
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

#if !REGEDIT_SYSTEM_CONPTY
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
#endif
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
