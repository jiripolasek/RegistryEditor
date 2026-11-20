// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit;
using JPSoftworks.Regedit.Controls.Omnibox;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AddressControl = JPSoftworks.Regedit.Controls.Omnibox.Omnibox;

namespace JPSoftworks.Omnibox.Sample;

public partial class App : Application
{
    internal static readonly OmniboxItem Workspace = new("Workspace", "space:home") { HasChildren = true };
    internal static readonly OmniboxItem Projects = new("Projects", "space:projects") { HasChildren = true };
    internal static readonly OmniboxItem Notes = new("Notes", "space:notes") { HasChildren = false };

    internal static readonly OmniboxItem Component = new(
        "An unusually long component name for checking a narrow address field", "space:component") { HasChildren = false };

    internal static readonly Dictionary<string, OmniboxItem[]> Paths = new()
    {
        [Workspace.Address] = [Workspace],
        [Projects.Address] = [Workspace, Projects],
        [Notes.Address] = [Workspace, Notes],
        [Component.Address] = [Workspace, Projects, Component]
    };

    private Window? _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var first = CreateAddressControl();
        var second = CreateAddressControl();
        var error = new InfoBar { Severity = InfoBarSeverity.Error };
        foreach (var control in new[] { first, second })
        {
            control.NavigationRequested += (_, request) =>
            {
                if (Paths.TryGetValue(request.Address, out var trail))
                {
                    control.SetLocation(request.Address, trail);
                    error.IsOpen = false;
                }
                else
                {
                    error.Message = "That location does not exist.";
                    error.IsOpen = true;
                }
            };
        }

        first.SetLocation(Component.Address, Paths[Component.Address]);
        second.SetLocation(Notes.Address, Paths[Notes.Address]);
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Workspace" });
        panel.Children.Add(first);
        panel.Children.Add(new TextBlock { Text = "Independent instance" });
        panel.Children.Add(second);
        panel.Children.Add(error);
        this._window = new Window { Title = "Standalone address control", Content = panel };
        this._window.AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 360));
        var arguments = Environment.GetCommandLineArgs();
        var checkIndex = Array.IndexOf(arguments, "--self-test");
        if (checkIndex >= 0 && checkIndex + 1 < arguments.Length)
        {
            var started = false;
            first.Loaded += async (_, _) =>
            {
                if (started)
                {
                    return;
                }

                started = true;
                await ComponentChecks.RunAsync(first, second, panel, arguments[checkIndex + 1]);
                this.Exit();
            };
        }

        this._window.Activate();
    }

    internal static AddressControl CreateAddressControl()
    {
        return new AddressControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ChildrenProvider = ChildrenAsync,
            SuggestionProvider = (text, token) => Task.FromResult<IReadOnlyList<OmniboxItem>>(
                Paths.Values.Select(path => path[^1]).Where(item =>
                    item.Label.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || item.Address.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray())
        };
    }

    internal static async Task<IReadOnlyList<OmniboxItem>> ChildrenAsync(string? address, CancellationToken token)
    {
        await Task.Delay(30, token);
        return address switch
        {
            null => (OmniboxItem[])[Workspace],
            "space:home" => (OmniboxItem[])[Projects, Notes],
            "space:projects" => (OmniboxItem[])[Component],
            _ => []
        };
    }
}
