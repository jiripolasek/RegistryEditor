// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.ColumnList.Sample;

public partial class App : Application
{
    private Window? _window;

    internal static Window? MainWindow { get; private set; }

    /// <summary>Set by <c>--input-test</c>: the self-test also drives the real mouse and keyboard.</summary>
    internal static bool InputTests { get; private set; }

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += (_, args) => File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "ColumnList.Sample.crash.txt"), args.Exception.ToString());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var iconCheckIndex = Array.IndexOf(arguments, "--icon-test");
        FrameworkElement page = iconCheckIndex >= 0 ? new Page() : new MainPage();
        this._window = MainWindow = new Window { Title = "Column list sample", Content = page };
        this._window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        InputTests = arguments.Contains("--self-test") && arguments.Contains("--input-test");
        if (InputTests && this._window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            var appWindow = this._window.AppWindow;
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            appWindow.Move(
                new Windows.Graphics.PointInt32(
                    area.X + Math.Max(0, area.Width - appWindow.Size.Width - 20),
                    area.Y + Math.Clamp(area.Height - appWindow.Size.Height, 0, 28)));
            presenter.IsAlwaysOnTop = true;
        }

        var checkIndex = iconCheckIndex >= 0 ? iconCheckIndex : Array.IndexOf(arguments, "--self-test");
        if (checkIndex >= 0 && checkIndex + 1 < arguments.Length)
        {
            var started = false;
            page.Loaded += async (_, _) =>
            {
                if (started)
                {
                    return;
                }

                started = true;
                if (iconCheckIndex >= 0)
                {
                    await CommandIconChecks.RunAsync(this._window, arguments[checkIndex + 1]);
                }
                else
                {
                    await ComponentChecks.RunAsync((MainPage)page, this._window, arguments[checkIndex + 1]);
                }

                this.Exit();
            };
        }

        this._window.Activate();
    }
}
