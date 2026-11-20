// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Snapshots;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.ViewModels.Browser;
using JPSoftworks.Regedit.ViewModels.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class RegistryHistoryBroadcasterTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registry)
    {
        var history = new RegistryHistoryBroadcaster();
        var first = NewWindow(history, new RegistryService());
        var second = NewWindow(history, new RegistryService());
        try
        {
            var local = new RegistryLocation();
            var source = first.OpenBrowser(local).Browser;
            var sibling = first.OpenBrowser(local).Browser;
            var peer = second.OpenBrowser(local).Browser;
            var otherView = second.OpenBrowser(local with { View = RegistryView.Registry32 }).Browser;
            var unloaded = second.OpenBrowser(local).Browser;
            var remote = second.OpenBrowser(local with { Machine = "SERVER" }).Browser;
            var remotePeer = first.OpenBrowser(local with { Machine = "server" }).Browser;
            foreach (var browser in new[] { source, sibling, peer, otherView, remote, remotePeer })
            {
                await browser.NavigateAsync(browser.Location, false);
            }

            var refreshed = new List<BrowserViewModel>();
            foreach (var browser in new[] { source, sibling, peer, otherView, unloaded, remote, remotePeer })
            {
                browser.TreeRefreshRequested += () => refreshed.Add(browser);
            }

            await history.PublishAsync(source, [Change(local)]);
            check(
                refreshed.Count == 3 && refreshed.Contains(sibling) && refreshed.Contains(peer) && refreshed.Contains(otherView),
                "History refresh reaches loaded tabs in both windows and registry views, excluding its source, other computers and unloaded tabs");
            refreshed.Clear();
            await history.PublishAsync(remote, [Change(remote.Location)]);
            check(refreshed.SequenceEqual([remotePeer]), "The window history filter compares computer names without case sensitivity");

            var delivered = false;
            var failing = new RegistryHistoryBroadcaster();
            failing.Changed += (_, _) => throw new IOException("Refresh fixture");
            failing.Changed += (_, _) =>
            {
                delivered = true;
                return Task.CompletedTask;
            };
            var reported = false;
            try
            {
                await failing.PublishAsync(source, [Change(local)]);
            }
            catch (IOException)
            {
                reported = true;
            }

            check(reported && delivered, "A failed refresh is reported after notifying the remaining window subscribers");
            var closed = ClosedWindow(history);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            check(!closed.IsAlive, "A closed window unsubscribes from the app singleton and can be collected");
        }
        finally
        {
            first.WindowClosed();
            second.WindowClosed();
        }

        if (registry)
        {
            await CheckUndoRedoAsync(check);
        }
    }

    private static async Task CheckUndoRedoAsync(Action<bool, string> check)
    {
        var root = RegistryLocation.Parse(@"HKCU\Software\RegeditModern.Tests\HistoryBroadcast_" + Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateTempSubdirectory("RegeditHistoryBroadcast-");
        var history = new RegistryHistoryBroadcaster();
        var first = NewWindow(history, new RegistryService { Journal = new RegistryJournal(folder.FullName) });
        var second = NewWindow(history, new RegistryService { Journal = new RegistryJournal(folder.FullName) });
        var prompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? pending = null;
        try
        {
            Registry.CurrentUser.CreateSubKey(root.SubKey).Dispose();
            var source = first.OpenBrowser(root).Browser;
            var sibling = first.OpenBrowser(root).Browser;
            var peer = second.OpenBrowser(root).Browser;
            var busy = second.OpenBrowser(root).Browser;
            foreach (var browser in new[] { source, sibling, peer, busy })
            {
                await browser.NavigateAsync(root, false);
            }

            source.EditPromptRequested += edit =>
            {
                var editor = (RegistryValueEditorViewModel)edit;
                editor.Name = "Broadcast";
                editor.Data = "01";
                return Task.FromResult(true);
            };
            await source.CreateValueCommand.ExecuteAsync("3");
            await sibling.RefreshCommand.ExecuteAsync(null);
            await peer.RefreshCommand.ExecuteAsync(null);
            busy.EditPromptRequested += _ => prompt.Task;
            pending = busy.CreateValueCommand.ExecuteAsync("3");
            var busyRefreshes = 0;
            busy.TreeRefreshRequested += () => busyRefreshes++;
            check(!busy.CanPrompt, "The refresh fixture holds a real edit prompt open");

            await source.UndoCommand.ExecuteAsync(null);
            check(
                !HasValue(source) && !HasValue(sibling) && !HasValue(peer) && busyRefreshes == 0,
                "Undo publishes to both window models, refreshes loaded sibling tabs and skips a busy browser");
            await source.RedoCommand.ExecuteAsync(null);
            check(
                HasValue(source) && HasValue(sibling) && HasValue(peer) && busyRefreshes == 0,
                "Redo publishes its restored state across windows before completing");
        }
        finally
        {
            prompt.TrySetResult(false);
            if (pending is not null)
            {
                await pending;
            }

            first.WindowClosed();
            second.WindowClosed();
            Registry.CurrentUser.DeleteSubKeyTree(root.SubKey, false);
            folder.Delete(true);
        }

        static bool HasValue(BrowserViewModel browser)
        {
            return browser.Entries.Any(entry => entry.Value?.Name == "Broadcast");
        }
    }

    private static MainWindowViewModel NewWindow(RegistryHistoryBroadcaster history, RegistryService registry)
    {
        var settings = new AppSettings();
        return new MainWindowViewModel(
            settings, new InvestigationStore(settings, () => { }), registry,
            location => new BrowserViewModel(registry, location, new RegistryEntryClipboard(), historyBroadcaster: history),
            NullLogger<MainWindowViewModel>.Instance, NotesStoreTests.CreateStore(settings), history);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ClosedWindow(RegistryHistoryBroadcaster history)
    {
        var window = NewWindow(history, new RegistryService());
        window.WindowClosed();
        return new WeakReference(window);
    }

    private static JournalEntry Change(RegistryLocation location)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new JournalState(
            new JournalScope(location), new RegistryBranchSnapshot(1, location, now, now, [], location.Machine ?? Environment.MachineName),
            []);
        return new JournalEntry(Guid.NewGuid(), now, "Revert", [state], [state], Kind: JournalOperation.Revert);
    }
}
