// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class NavigationHistoryTests
{
    public static async Task RunAsync(Action<bool, string> check, Action<Action, string> reject, bool registryTests)
    {
        var root = RegistryLocation.Parse("HKCU");
        var a = root.Child("A");
        var b = root.Child("B");
        var c = root.Child("C");
        var history = new NavigationHistory(root);
        check(history.GetEntries(false).Count == 0 && history.GetEntries(true).Count == 0, "New history has no menu entries");
        foreach (var location in new[] { a, b, a, c })
        {
            history.Navigate(location);
        }

        var back = history.GetEntries(false);
        check(
            back.Select(entry => entry.Location).SequenceEqual(new[] { a, b, a, root })
            && back.Select(entry => entry.Index).SequenceEqual(new[] { 3, 2, 1, 0 }),
            "Back history is nearest-first and distinguishes repeated visits");
        history.MoveTo(1);
        check(
            history.Current == a && history.BackTarget == root && history.ForwardTarget == b,
            "History jump moves the cursor without adding a visit");
        check(
            history.GetEntries(true).Select(entry => entry.Location).SequenceEqual(new[] { b, a, c }),
            "Forward history retains all skipped visits in order");
        history.MoveTo(3);
        check(
            history.Current == a && history.BackTarget == b && history.ForwardTarget == c,
            "Selecting a repeated address reaches the requested visit");
        reject(() => history.MoveTo(-1), "History rejects negative jump indices");
        reject(() => history.MoveTo(5), "History rejects jump indices past the end");
        check(
            history.Current == a && history.BackTarget == b && history.GetTarget(-1) is null && history.GetTarget(5) is null,
            "Rejected jumps leave the history cursor intact");
        history.MoveTo(1);
        var replacement = root.Child("Replacement");
        history.Navigate(replacement);
        check(
            !history.CanGoForward && history.GetTarget(2) == replacement && history.GetTarget(3) is null,
            "New navigation after a jump truncates the forward branch");
        check(
            back.Select(entry => entry.Location).SequenceEqual(new[] { a, b, a, root }), "Open history menus retain an immutable snapshot");

        var computer = root.GetComputer();
        var remote = RegistryLocation.Parse("\\\\SERVER", RegistryView.Registry32);
        var other = RegistryLocation.Parse("\\\\OTHER", RegistryView.Registry64);
        var view = new BrowserViewModel(new RegistryService(), computer);
        var backChanges = 0;
        var forwardChanges = 0;
        view.BackCommand.CanExecuteChanged += (_, _) => backChanges++;
        view.ForwardCommand.CanExecuteChanged += (_, _) => forwardChanges++;
        check(!view.BackCommand.CanExecute(null) && !view.ForwardCommand.CanExecute(null), "Both history commands start disabled");
        await view.NavigateAsync(remote);
        check(
            backChanges > 0 && view.BackCommand.CanExecute(null) && !view.ForwardCommand.CanExecute(null),
            "A committed navigation enables the bound Back command");
        await view.NavigateAsync(other);
        var target = view.GetHistory(false)[0];
        check(target.Location == remote, "History menu entries retain computer and registry-view identity");
        forwardChanges = 0;
        await view.NavigateHistoryAsync(target.Index, target.Location);
        check(forwardChanges > 0 && view.ForwardCommand.CanExecute(null), "A history jump enables the bound Forward command");
        check(view.Location == remote && view.CanGoBack && view.CanGoForward, "Menu jumps update navigation state");
        var stale = view.GetHistory(true)[0];
        await view.NavigateAsync(computer);
        await view.NavigateHistoryAsync(stale.Index, stale.Location);
        await view.NavigateHistoryAsync(int.MaxValue, remote);
        check(
            view.Location == computer && !view.CanGoForward && view.Error is null,
            "Stale menu entries cannot navigate to a replaced history slot");

        if (!registryTests)
        {
            return;
        }

        var service = new RegistryService();
        var software = RegistryLocation.Parse("HKCU\\Software");
        var fixture = software.Child("RegeditNavigationTests." + Guid.NewGuid().ToString("N"));
        service.CreateKey(software, fixture.Name);
        try
        {
            foreach (var name in new[] { "A", "B", "C" })
            {
                service.CreateKey(fixture, name);
            }

            view = new BrowserViewModel(service, fixture);
            foreach (var name in new[] { "A", "B", "C" })
            {
                await view.NavigateAsync(fixture.Child(name));
            }

            target = view.GetHistory(false).Single(entry => entry.Location.Name == "A");
            var before = view.GetHistory(false).ToArray();
            service.DeleteKey(target.Location);
            await view.NavigateHistoryAsync(target.Index, target.Location);
            check(
                view.Location == fixture.Child("C")
                && view.Error is not null
                && view.GetHistory(false).SequenceEqual(before)
                && !view.CanGoForward, "Failed history jumps preserve the location and both stacks");
            service.CreateKey(fixture, "A");
            await view.NavigateHistoryAsync(target.Index, target.Location);
            check(
                view.Location == target.Location
                && view.Error is null
                && view.GetHistory(true).Select(entry => entry.Location.Name).SequenceEqual(new[] { "B", "C" }),
                "A restored history target can be revisited without losing intermediate visits");
            target = view.GetHistory(true).Last();
            var pending = view.NavigateHistoryAsync(target.Index, target.Location);
            await view.NavigateAsync(computer);
            await pending;
            check(view.Location == computer && !view.CanGoForward, "A newer navigation supersedes a pending history jump");
        }
        finally
        {
            service.DeleteKey(fixture);
        }
    }
}
