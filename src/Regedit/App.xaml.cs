// ------------------------------------------------------------
// Copyright (c) Jiří Polášek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure;
using JPSoftworks.Regedit.Infrastructure.Interop;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels;
using JPSoftworks.Regedit.ViewModels.Shell;
using JPSoftworks.Regedit.ViewModels.Tools;
using JPSoftworks.Regedit.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace JPSoftworks.Regedit;

public partial class App : Application
{
    private readonly HashSet<Window> _windows = [];
    private readonly ServiceProvider _services;
    private readonly ILogger<App> _logger;
    private readonly JumpListService _jumpList;
    private int _servicesStopped;

    public App()
    {
        this._services = AppServices.Configure(new WindowsJumpListBackend()).BuildServiceProvider(true);
        this._logger = this._services.GetRequiredService<ILogger<App>>();
        this._jumpList = this._services.GetRequiredService<JumpListService>();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => this.StopServices();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                this._logger.LogFailure(LogLevel.Critical, "Unhandled process exception", ex);
            }
        };
        this.UnhandledException += (_, args) => this._logger.LogFailure(
            LogLevel.Critical, "Unhandled UI exception", args.Exception);
        this._logger.LogInformation("Registry Editor started");
        this.InitializeComponent();
#if DEBUG
        this.DebugSettings.BindingFailed += (_, args) =>
        {
            Debug.WriteLine(args.Message);
            this._logger.LogWarning("XAML binding failed");
        };
#endif
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
#if DEBUG || REGEDIT_UI_TESTS
            if (Environment.GetCommandLineArgs().Contains("--notification-prototype", StringComparer.Ordinal))
            {
                this.OpenNotificationPrototype();
                return;
            }
#endif

            RegistryLocation? location = null;
            string? startupError = null;
            try
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                var arguments
                    = JumpListNative.IsPackaged && activation.Kind == ExtendedActivationKind.Launch &&
                      activation.Data is ILaunchActivatedEventArgs launch
                        ? RegistryLaunch.SplitArguments(launch.Arguments)
                        : Environment.GetCommandLineArgs().Skip(1).ToArray();
                if (JumpListEntries.IsAdministratorTask(arguments))
                {
                    try
                    {
                        using var process
                            = Process.Start(
                                  RegistryLaunch.CreateElevationStartInfo(
                                      Environment.ProcessPath!, RegistryLocation.Parse("Computer"))) ??
                              throw new InvalidOperationException("Couldn't open an administrator window.");
                        this.StopServices();
                        this.Exit();
                        return;
                    }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                    {
                        this.StopServices();
                        this.Exit();
                        return;
                    }
                    catch (Exception ex)
                    {
                        this._logger.LogFailure(LogLevel.Warning, "Launch administrator task", ex);
                        startupError = $"Couldn't open an administrator window: {ex.Message}";
                        location = RegistryLocation.Parse("Computer");
                    }
                }
                else
                {
                    location = activation.Kind == ExtendedActivationKind.Protocol &&
                               activation.Data is IProtocolActivatedEventArgs protocol
                        ? RegistryLaunch.ParseUri(protocol.Uri.OriginalString)
                        : RegistryLaunch.ParseArguments(arguments);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or Win32Exception)
            {
                this._logger.LogFailure(LogLevel.Warning, "Parse activation", ex);
                startupError = ex.Message;
                location = RegistryLocation.Parse("Computer");
            }

#if !REGEDIT_UI_TESTS
            try
            {
                if (!JumpListNative.IsPackaged &&
                    !RegistryLaunch.IsProtocolRegisteredForExecutable(Environment.ProcessPath))
                {
                    ActivationRegistrationManager.RegisterForProtocolActivation(
                        RegistryLaunch.Scheme, Environment.ProcessPath + ",0", "Modern Registry Editor", string.Empty);
                }
            }
            catch (Exception ex)
            {
                this._logger.LogFailure(LogLevel.Warning, "Register protocol", ex);
                startupError = $"{startupError}\n\nCouldn't register regedit2:// links: {ex.Message}".Trim();
            }
#endif

            this.OpenWindow(location, startupError);
        }
        catch (Exception ex)
        {
            this._logger.LogFailure(LogLevel.Critical, "Launch main window", ex);
            throw;
        }
    }

    private void OpenWindow(RegistryLocation? location, string? startupError)
    {
        var scope = this._services.CreateScope();
        MainWindow? window = null;
        try
        {
            var services = scope.ServiceProvider;
            var favorites = services.GetRequiredService<FavoritesStore>();
            window = new MainWindow(
                services.GetRequiredService<ILogger<MainWindow>>(), services.GetRequiredService<AppSettings>(),
                services.GetRequiredService<RegistryService>(), services.GetRequiredService<JournalViewModel>(),
                favorites, services.GetRequiredService<SettingsViewModel>(),
                services.GetRequiredService<MainWindowViewModel>(),
                services.GetRequiredService<Func<WatchSurfaceViewModel>>(), location, startupError);
            this._windows.Add(window);
#if !REGEDIT_UI_TESTS
            try
            {
                if (!JumpListNative.IsPackaged)
                {
                    JumpListNative.SetWindowIdentity(window.Handle, Environment.ProcessPath!);

                    window.Closed += (_, _) =>
                    {
                        try
                        {
                            JumpListNative.SetWindowIdentity(window.Handle, null);
                        }
                        catch (Exception ex)
                        {
                            this._logger.LogFailure(LogLevel.Warning, "Clear taskbar identity", ex);
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                this._logger.LogFailure(LogLevel.Warning, "Set taskbar identity", ex);
            }

            EventHandler favoritesChanged = (_, _) => _ = this._jumpList.UpdateAsync();
            favorites.Changed += favoritesChanged;
#endif
            window.Closed += (_, _) =>
            {
#if !REGEDIT_UI_TESTS
                favorites.Changed -= favoritesChanged;
#endif
                this._windows.Remove(window);
                scope.Dispose();
                if (this._windows.Count == 0)
                {
                    this.StopServices();
                }
            };
            window.Activate();
#if !REGEDIT_UI_TESTS
            _ = this._jumpList.UpdateAsync();
#endif
        }
        catch
        {
            if (window is not null)
            {
                this._windows.Remove(window);
            }

            scope.Dispose();
            throw;
        }
    }

    private void StopServices()
    {
        if (Interlocked.Exchange(ref this._servicesStopped, 1) != 0)
        {
            return;
        }

        this._logger.LogInformation("Registry Editor stopped");
        this._services.Dispose();
    }
}
