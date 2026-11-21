// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

#if REGEDIT_EXPERIMENTAL_TERMINAL
using System.Text;
using JPSoftworks.Regedit.Core.Export;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.Views.Pages;
using JPSoftworks.Regedit.Views.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Views.Shell;

public sealed partial class MainWindow
{
    private const string PowerShellGlyph = "\uE756";
    private static readonly RegistryLocation DefaultPowerShellLocation = new(RegistryHive.CurrentUser);

    private void InitializeTerminalMenu()
    {
        this.BottomPanelMenu.Items.Add(this.CreatePowerShellMenuItem());
    }

    private MenuFlyoutItem CreatePowerShellMenuItem()
    {
        var shell = new MenuFlyoutItem
        {
            Text = "PowerShell", Icon = new FontIcon { Glyph = PowerShellGlyph }, KeyboardAcceleratorTextOverride = "Ctrl+;"
        };
        shell.Click += (_, _) => this.OpenPowerShell();
        return shell;
    }

    internal void OpenPowerShell(RegistryLocation? location = null)
    {
        location ??= this.ViewModel.VisibleBrowser?.Location;
        this.OpenPowerShell(location is { IsComputer: false, Machine: null } ? location : DefaultPowerShellLocation, null);
    }

    private void OpenPowerShell(RegistryLocation location, PowerShellRun? run)
    {
        if (this.ViewModel.IsClosing || this.Dialogs.IsOpen)
        {
            return;
        }

        this.ViewModel.SelectBottomTab(new PowerShellTab(location, run));
    }

    private PowerShellView CreatePowerShellView(PowerShellTab tab)
    {
        var use32Bit = tab.Location.View == RegistryView.Registry32 || !Environment.Is64BitOperatingSystem;
        var shellName = tab.UseWindowsPowerShell ? "Windows PowerShell 5.1" : "PowerShell 7";
        string? version = null;
        var error = "Windows PowerShell 5.1 could not be found.";
        var executable = tab.UseWindowsPowerShell
            ? PowerShellDiscovery.FindWindowsPowerShell(use32Bit)
            : PowerShellDiscovery.FindPowerShell7(this.Settings.PowerShellPath, use32Bit, out error, out version);
        var shellDetails = executable ?? "No executable selected.";
        if (version is { Length: > 0 })
        {
            shellDetails = $"Version {version}{Environment.NewLine}{shellDetails}";
        }

        shellDetails += $"{Environment.NewLine}{Environment.NewLine}Starting key{Environment.NewLine}{tab.Location.Address}";

        string? commandLine = null;
        if (executable is not null)
        {
            var openKey = RegistryScripts.PowerShellLocation(tab.Location);
            var openDefault = RegistryScripts.PowerShellLocation(DefaultPowerShellLocation);
            var command = $"try {{ {openKey} -ErrorAction Stop }} catch {{ Write-Warning $_.Exception.Message; {openDefault} }}";
            if (tab.Run is { } run)
            {
                command += Environment.NewLine + run.StartupCommand;
            }

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            commandLine = $"\"{executable}\" -NoLogo -NoProfile -NoExit -EncodedCommand {encoded}";
        }

        return new PowerShellView(shellName, shellDetails, commandLine, error, tab.Run, CreateRecoveryActions);

        FrameworkElement CreateRecoveryActions()
        {
            var actions = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal };
            actions.Children.Add(CreateButton("Retry", () => Restart(false)));
            actions.Children.Add(CreateButton("Terminal settings", () =>
            {
                this.ViewModel.OpenSettingsCommand.Execute(null);
                (this.ActiveTabContent.Content as SettingsPage)?.ShowTerminalSettings();
            }));
            if (!tab.UseWindowsPowerShell && this.Settings.OfferWindowsPowerShellFallback &&
                PowerShellDiscovery.FindWindowsPowerShell(use32Bit) is not null)
            {
                actions.Children.Add(CreateButton("Use Windows PowerShell 5.1", () => Restart(true)));
            }

            return actions;
        }

        void Restart(bool useWindowsPowerShell)
        {
            if (this.ViewModel.ActiveBottomTab != tab || this.ViewModel.IsClosing || this.Dialogs.IsOpen)
            {
                return;
            }

            tab.UseWindowsPowerShell = useWindowsPowerShell && this.Settings.OfferWindowsPowerShellFallback;
            this.ReleaseView(tab);
            this.ShowBottomContent();
        }

        static Button CreateButton(string label, Action action)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => action();
            return button;
        }
    }

    internal async Task ReviewPowerShellScriptAsync(Func<RegistryDifference[]> selection)
    {
        try
        {
            var changes = selection();
            var script = RegistryScripts.PowerShell(changes);
            var preview = new TextBox
            {
                Text = script,
                Header = "Generated script",
                FontFamily = new FontFamily("Consolas"),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                Height = 290
            };
            AutomationProperties.SetAutomationId(preview, "PowerShellScriptPreview");
            ScrollViewer.SetHorizontalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
            var dialog = new AppDialog(
                "Review PowerShell script",
                new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = this.IsAdministrator
                                ? "This script applies the selected changes with administrator permissions."
                                : "This script applies the selected changes with your current permissions.",
                            TextWrapping = TextWrapping.Wrap
                        },
                        preview
                    }
                },
                "Run in PowerShell",
                "Cancel",
                ContentDialogButton.Close);
            if (await this.Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
            {
                var targets = changes.Select(change => change.Location).Distinct().ToArray();
                this.OpenPowerShell(DefaultPowerShellLocation, new PowerShellRun(script, targets));
            }
        }
        catch (Exception ex)
        {
            this.Logger.LogFailure(LogLevel.Warning, "Review PowerShell script", ex);
            this.ShowApplicationNotice("Could not open PowerShell script", ex.Message, NoticeKind.Error);
        }
    }

    private void SetTerminalShellButton(PowerShellView? terminal)
    {
        this.TerminalShellButton.Flyout?.Hide();
        this.TerminalShellButton.Visibility = terminal is null ? Visibility.Collapsed : Visibility.Visible;
        if (terminal is not null)
        {
            this.TerminalShellButton.Content = terminal.ShellName;
            AutomationProperties.SetName(this.TerminalShellButton, $"{terminal.ShellName} shell details");
            this.TerminalShellName.Text = terminal.ShellName;
            this.TerminalShellDetails.Text = terminal.ShellDetails;
        }
    }

    private void UpdateTerminalVisibility()
    {
        if (this.BottomContent.Content is PowerShellView view)
        {
            view.SetSurfaceVisible(this.ViewModel.IsBottomPaneOpen && !this.Dialogs.IsOpen);
        }
    }

    private sealed class PowerShellTab(RegistryLocation location, PowerShellRun? run)
        : WindowTab(run is null ? "PowerShell" : "PowerShell script", PowerShellGlyph)
    {
        public override string Tooltip => this.Location.Address;

        internal RegistryLocation Location { get; } = location;

        internal PowerShellRun? Run { get; } = run;

        internal bool UseWindowsPowerShell { get; set; }
    }
}
#endif
