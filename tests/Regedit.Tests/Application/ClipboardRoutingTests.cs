// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class ClipboardRoutingTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool useRegistry)
    {
        var root = RegistryLocation.Parse(@"HKCU\Software\RegeditClipboard-" + Guid.NewGuid().ToString("N"));
        var remote = RegistryLocation.Parse(@"\\SERVER\HKLM\Software\CaseSensitiveSpelling", RegistryView.Registry32);
        check(
            remote.IsSameLocation(remote with { Machine = "server", SubKey = remote.SubKey.ToLowerInvariant() }) &&
            !remote.IsSameLocation(remote with { Hive = RegistryHive.Users }) &&
            !remote.IsSameLocation(remote with { View = RegistryView.Registry64 }) &&
            !remote.IsSameLocation(remote.GetComputer()) && !remote.IsSameLocation(null),
            "Registry location comparison ignores machine and key casing while preserving hive view and Computer identity");
        var clipboard = new RegistryEntryClipboard();
        var browser = new BrowserViewModel(new RegistryService(), root, clipboard);
        var other = new BrowserViewModel(new RegistryService(), root.Child("Other"), clipboard);
        check(
            !browser.PasteCommand.CanExecute(null) && !browser.PasteIntoCommand.CanExecute(root),
            "Both Paste routes require a stored registry clipboard item");
        var notifications = 0;
        other.PasteIntoCommand.CanExecuteChanged += (_, _) => notifications++;
        string? text = null;
        browser.CopyTextRequested += copied => text = copied;
        var value = new RegistryEntry(null, new RegistryValue("ListValue", (RegistryValueType)99, [0, 255, 0, 65]));
        browser.SelectedEntry = value;
        browser.CopyTreeKeyCommand.Execute(root.Child("TreeSource"));
        check(
            clipboard.Entry?.Key == root.Child("TreeSource") && text == root.Child("TreeSource").Address &&
            notifications > 0 && other.PasteIntoCommand.CanExecute(root),
            "Tree Copy captures the selected key and path independently of the list and notifies typed Paste");
        var copiedKey = clipboard.Entry;
        foreach (var target in new RegistryLocation?[] { null, root.GetComputer() })
        {
            check(
                !browser.CopyTreeKeyCommand.CanExecute(target) && !browser.PasteIntoCommand.CanExecute(target),
                "Null and virtual Computer targets disable typed registry Copy and Paste");
            browser.CopyTreeKeyCommand.Execute(target);
            await browser.PasteIntoCommand.ExecuteAsync(target);
        }

        check(
            ReferenceEquals(clipboard.Entry, copiedKey) &&
            browser.CopyTreeKeyCommand.CanExecute(RegistryLocation.Parse("HKCU")),
            "Disabled tree Copy preserves the clipboard and real hive keys retain their copy eligibility");
        foreach (var selection in new RegistryEntry[][]
                 {
                     [], [new RegistryEntry(null, null, true)],
                     [new RegistryEntry(null, null, ParentLocation: root)], [value, copiedKey!]
                 })
        {
            browser.SetSelection(selection);
            check(!browser.CopyCommand.CanExecute(null), "List Copy requires exactly one stored registry item");
            browser.CopyCommand.Execute(null);
            check(ReferenceEquals(clipboard.Entry, copiedKey), "Disabled list Copy preserves the registry clipboard");
        }

        browser.SelectedEntry = value;
        browser.CopyCommand.Execute(null);
        check(
            !ReferenceEquals(clipboard.Entry, value) && clipboard.Entry?.Value == value.Value && text == value.Data,
            "List Copy retains exact selected registry bytes and its displayed text");
        var pending = new TaskCompletionSource<bool>();
        var prompts = 0;
        browser.EditPromptRequested += _ =>
        {
            prompts++;
            return pending.Task;
        };
        var paste = browser.PasteIntoCommand.ExecuteAsync(root.Child("ExplicitDestination"));
        check(
            !browser.PasteCommand.CanExecute(null) && !browser.PasteIntoCommand.CanExecute(root) && prompts == 1,
            "An open Paste dialog disables both current-container and typed-destination Paste");
        await browser.PasteCommand.ExecuteAsync(null);
        check(prompts == 1, "Calling disabled current-container Paste cannot open a second dialog");
        pending.SetResult(false);
        await paste;
        check(
            browser.PasteCommand.CanExecute(null) && browser.PasteIntoCommand.CanExecute(root),
            "Canceling Paste restores both command routes without writing");
        browser.CancelPending();
        other.CancelPending();
        if (!useRegistry)
        {
            return;
        }

        var registry = new RegistryService();
        registry.CreateKey(root.Parent!, root.Name);
        try
        {
            var source = root.Child("Source");
            var tree = root.Child("TreeDestination");
            var list = root.Child("ListDestination");
            foreach (var key in new[] { source, tree, list })
            {
                registry.CreateKey(root, key.Name);
            }

            var raw = new RegistryValue("Raw", (RegistryValueType)99, [0, 255, 0, 65]);
            registry.Write(source, raw, null);
            registry.Write(list, new RegistryValue("ListSelection", RegistryValueType.Binary, [9]), null);
            clipboard = new RegistryEntryClipboard();
            browser = new BrowserViewModel(registry, list, clipboard);
            await browser.NavigateAsync(list);
            browser.SelectedEntry = browser.Entries.Single(row => row.Value?.Name == "ListSelection");
            var selected = browser.SelectedEntry;
            var entries = browser.Entries;
            var branches = new List<RegistryLocation>();
            var rebuilds = 0;
            browser.TreeBranchChanged += (destination, _) =>
            {
                branches.Add(destination);
                return Task.CompletedTask;
            };
            browser.TreeRefreshRequested += () => rebuilds++;
            var name = "TreeClone";
            var accept = true;
            var errors = 0;
            Action? before = null;
            string? destinationName = null;
            browser.EditPromptRequested += prompt =>
            {
                if (prompt.Error is not null)
                {
                    errors++;
                    return Task.FromResult(false);
                }

                var form = (RegistryNamePrompt)prompt;
                destinationName = form.PasteDestination;
                form.Name = name;
                var action = before;
                before = null;
                action?.Invoke();
                return Task.FromResult(accept);
            };
            browser.CopyTreeKeyCommand.Execute(source);
            before = () => clipboard.Entry = value;
            await browser.PasteIntoCommand.ExecuteAsync(tree);
            check(
                registry.ReadValue(tree.Child("TreeClone"), "Raw")!.SameData(raw) &&
                !registry.SubKeys(list).Contains("TreeClone") && destinationName == tree.Name,
                "Typed tree Paste captures its source and explicit destination before the prompt");
            check(
                branches.SequenceEqual([tree]) && rebuilds == 0 && ReferenceEquals(browser.Entries, entries) &&
                ReferenceEquals(browser.SelectedEntry, selected) && browser.Location == list,
                "Divergent tree key Paste refreshes only its destination branch and preserves the unrelated list");

            browser.SelectedEntry = new RegistryEntry(null, raw);
            browser.CopyCommand.Execute(null);
            name = "TreeValue";
            branches.Clear();
            await browser.PasteIntoCommand.ExecuteAsync(tree);
            check(
                registry.ReadValue(tree, name)!.SameData(raw) && registry.ReadValue(list, name) is null &&
                branches.Count == 0,
                "Typed tree value Paste preserves type and bytes without rebuilding any tree branch");
            accept = false;
            name = "Canceled";
            await browser.PasteIntoCommand.ExecuteAsync(tree);
            check(
                registry.ReadValue(tree, name) is null && branches.Count == 0,
                "Canceling typed Paste leaves the destination and tree branches unchanged");
            accept = true;
            name = "TreeValue";
            await browser.PasteIntoCommand.ExecuteAsync(tree);
            check(
                errors == 1 && registry.ReadValue(tree, name)!.SameData(raw),
                "Typed Paste retains the core overwrite refusal and existing bytes");

            browser.CopyTreeKeyCommand.Execute(source);
            name = "Nested";
            await browser.PasteIntoCommand.ExecuteAsync(source with { View = RegistryView.Registry32 });
            check(
                errors == 2 && !registry.SubKeys(source).Contains(name),
                "Typed key Paste retains alias and shared-view descendant protection before mutation");
            browser.SelectedEntry = new RegistryEntry(null, raw);
            browser.CopyCommand.Execute(null);
            name = "ListPaste";
            await browser.PasteCommand.ExecuteAsync(null);
            check(
                registry.ReadValue(list, name)!.SameData(raw) && registry.ReadValue(tree, name) is null &&
                browser.Entries.Any(row => row.Value?.Name == name),
                "List Paste still targets and refreshes the current container with the copied type and bytes");
            entries = browser.Entries;
            name = "CaseAliasPaste";
            await browser.PasteIntoCommand.ExecuteAsync(list with { SubKey = list.SubKey.ToLowerInvariant() });
            check(
                registry.ReadValue(list, name)!.SameData(raw) && !ReferenceEquals(browser.Entries, entries) &&
                browser.Entries.Any(row => row.Value?.Name == name),
                "Pasting through a differently cased address refreshes the same visible registry key");
            browser.CancelPending();
        }
        finally
        {
            registry.DeleteKey(root);
        }
    }
}
