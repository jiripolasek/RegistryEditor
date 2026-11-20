// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json;
using JPSoftworks.Regedit.Controls.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace JPSoftworks.ColumnList.Sample;

internal static class CommandIconChecks
{
    internal static async Task RunAsync(Window window, string output)
    {
        var passed = new List<string>();
        string? failure = null;
        try
        {
            await RunAsync(
                window, (condition, name) =>
                {
                    if (!condition)
                    {
                        throw new InvalidOperationException(name);
                    }

                    passed.Add(name);
                });
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var stream = File.Create(output);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray("passed");
        foreach (var name in passed)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
        writer.WriteString("failure", failure);
        writer.WriteEndObject();
        Environment.ExitCode = failure is null ? 0 : 1;
    }

    internal static async Task RunAsync(Window window, Action<bool, string> check)
    {
        var original = window.Content;
        var panel = new StackPanel();
        var bar = new CommandBar { Width = 800, DefaultLabelPosition = CommandBarDefaultLabelPosition.Right };
        for (var index = 0; index < 5; index++)
        {
            bar.PrimaryCommands.Add(new AppBarButton { Label = $"Command {index}", Width = 100 });
        }

        var icon = new CommandIcon { Symbol = Symbol.Save };
        var host = new AppBarButton { Label = "Owned icon", Width = 140, Icon = icon };
        icon.SetHost(host);
        bar.PrimaryCommands.Add(host);
        panel.Children.Add(bar);
        window.Content = panel;
        var accent = new AccessibilitySettings().HighContrast ? null : (Style)Application.Current.Resources["CommandIconAccentStyle"];
        try
        {
            await WaitAsync(() => icon.IsLoaded);
            check(ReferenceEquals(icon.Style, accent), "An explicitly owned command icon follows the enabled host");
            host.IsEnabled = false;
            await WaitAsync(() => icon.Style is null);
            check(icon.Style is null, "A disabled owner restores the native icon foreground");
            host.IsEnabled = true;
            icon.IsCompact = true;
            check(icon.Style is null, "Compact icons retain the native foreground");
            icon.IsCompact = false;
            check(ReferenceEquals(icon.Style, accent), "Leaving compact mode restores the expected accent policy");

            for (var cycle = 0; cycle < 12; cycle++)
            {
                panel.RequestedTheme = cycle % 2 == 0 ? ElementTheme.Light : ElementTheme.Dark;
                bar.Width = 160;
                await WaitAsync(() => host.IsInOverflow);
                bar.IsOpen = true;
                await WaitAsync(() => icon.IsLoaded);
                await CollectAsync();
                host.IsEnabled = false;
                await WaitAsync(() => icon.Style is null);
                check(icon.Style is null, $"Overflow icon follows its disabled owner after collection ({cycle})");
                host.IsEnabled = true;
                bar.IsOpen = false;
                bar.Width = 800;
                await WaitAsync(() => !host.IsInOverflow && icon.IsLoaded);
                panel.Children.Clear();
                await WaitAsync(() => !icon.IsLoaded);
                await CollectAsync();
                panel.Children.Add(bar);
                await WaitAsync(() => icon.IsLoaded);
                host.IsEnabled = false;
                await WaitAsync(() => icon.Style is null);
                check(icon.Style is null, $"Reloaded icon restores the owner subscription ({cycle})");
                host.IsEnabled = true;
                await WaitAsync(() => ReferenceEquals(icon.Style, accent));
                check(ReferenceEquals(icon.Style, accent), $"Reloaded icon keeps its enabled and theme policy ({cycle})");
            }

            var nextHost = new AppBarButton { Label = "New owner", Width = 140 };
            host.Icon = null;
            nextHost.Icon = icon;
            icon.SetHost(nextHost);
            bar.PrimaryCommands.Clear();
            bar.PrimaryCommands.Add(nextHost);
            await WaitAsync(() => icon.IsLoaded);
            host.IsEnabled = false;
            check(ReferenceEquals(icon.Style, accent), "The former owner's enabled state does not override the new owner");
            nextHost.IsEnabled = false;
            await WaitAsync(() => icon.Style is null);
            check(icon.Style is null, "Changing owners subscribes to the new owner");
        }
        finally
        {
            bar.IsOpen = false;
            window.Content = original;
        }
    }

    private static async Task CollectAsync()
    {
        await Task.Run(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        });
        await Task.Delay(40);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The command icon did not reach the expected lifecycle state.");
            }

            await Task.Delay(20);
        }
    }
}
