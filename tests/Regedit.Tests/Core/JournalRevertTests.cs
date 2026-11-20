// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Core;

internal static class JournalRevertTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var folder = Directory.CreateTempSubdirectory("Regedit.JournalRelease.");
        var id = Guid.NewGuid();
        var shared = RegistryLocation.Parse($@"HKCU\Software\RegeditJournal-{id:N}");
        var split = RegistryLocation.Parse($@"HKCU\Software\Classes\CLSID\{id:B}");
        using var user64 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var user32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
        using var identity = WindowsIdentity.GetCurrent();
        var alias = RegistryLocation.Parse($@"HKU\{identity.User!.Value}\{shared.SubKey}");
        var raw = new RegistryService();
        try
        {
            user64.CreateSubKey(shared.SubKey).Dispose();
            user64.CreateSubKey(split.SubKey).Dispose();
            user32.CreateSubKey(split.SubKey).Dispose();
            CheckHistory(shared, shared, "same path", true);
            CheckHistory(shared, alias, "HKCU and HKU", true);
            CheckHistory(shared, alias, "legacy HKCU and HKU", true, true);
            CheckHistory(shared, shared with { View = RegistryView.Registry32 }, "shared views", true);
            CheckHistory(split, split with { View = RegistryView.Registry32 }, "redirected views", false);
            CheckHistory(split, RegistryLocation.Parse($@"HKCR\CLSID\{id:B}"), "merged classes", true);

            var missing = shared.Child("Missing");
            RefusesCopy(missing, alias.Child("Missing"));
            raw.CreateKey(shared, "Source");
            var source = shared.Child("Source");
            var aliasSource = alias.Child("Source");
            RefusesCopy(source, aliasSource);
            check(raw.SubKeys(source).Length == 0, "Alias paste refuses before creating a destination");
            raw.CopyKey(source, alias, "Sibling");
            check(raw.SubKeys(shared).Contains("Sibling"), "Alias paste permits an independent sibling");
            check(
                RegistryJournal.Overlaps(new JournalScope(source), new JournalScope(aliasSource.Child("Missing"), ["value"]))
                && !RegistryJournal.Overlaps(new JournalScope(source, ["A"]), new JournalScope(aliasSource, ["B"])),
                "Alias conflict checks cover descendants and distinguish independent values");
            await CheckCommandsAsync(shared, Path.Combine(folder.FullName, "commands"), check);

            void RefusesCopy(RegistryLocation sourceKey, RegistryLocation parent)
            {
                try
                {
                    raw.CopyKey(sourceKey, parent, "Copy");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("copied into itself"))
                {
                    check(true, "Key copy guard recognizes " + sourceKey.Name + " through its HKU alias");
                    return;
                }

                throw new InvalidOperationException("The key copy guard accepted its own descendant.");
            }

            void CheckHistory(RegistryLocation first, RegistryLocation later, string name, bool sharedValue, bool legacy = false)
            {
                var zero = Number(0);
                var one = Number(1);
                var two = Number(2);
                raw.Write(first, zero, null);
                var visible = raw.ReadValue(later, name);
                check(visible is not null == sharedValue, name + " fixture verifies physical sharing");
                if (!sharedValue)
                {
                    raw.Write(later, one, null);
                }

                var journal = new RegistryJournal(Path.Combine(folder.FullName, name));
                var writer = new RegistryService { Journal = journal };
                writer.Write(first, one, zero);
                var entry = journal.ReadEntries().Single();
                writer.Write(later, two, one);
                writer.Write(later, one, two);
                if (legacy)
                {
                    foreach (var path in Directory.GetFiles(Path.Combine(folder.FullName, name), "*.json"))
                    {
                        EditFile(
                            path, json =>
                            {
                                json.Remove("Kind");
                                foreach (var state in json["Before"]!.AsArray().Concat(json["After"]!.AsArray()))
                                {
                                    state!["Scope"]!.AsObject().Remove("KeyPaths");
                                }
                            });
                    }
                }

                var blocked = false;
                try
                {
                    journal.Revert(entry.Id);
                }
                catch (IOException ex) when (ex.Message.Contains("later journal entry"))
                {
                    blocked = true;
                }

                check(
                    blocked == sharedValue
                    && raw.ReadValue(first, name)!.Data[0] == (sharedValue ? 1 : 0)
                    && raw.ReadValue(later, name)!.Data[0] == 1, name + " protects newer edits and permits independent Revert");

                RegistryValue Number(byte value)
                {
                    return new RegistryValue(name, RegistryValueType.DWord, [value, 0, 0, 0]);
                }
            }
        }
        finally
        {
            user64.DeleteSubKeyTree(shared.SubKey, false);
            user64.DeleteSubKeyTree(split.SubKey, false);
            user32.DeleteSubKeyTree(split.SubKey, false);
            folder.Delete(true);
        }
    }

    private static async Task CheckCommandsAsync(RegistryLocation root, string folder, Action<bool, string> check)
    {
        var journal = new RegistryJournal(folder);
        var writer = new RegistryService { Journal = journal };
        var raw = new RegistryService();
        var model = new JournalViewModel(journal, NullLogger<JournalViewModel>.Instance);
        var refreshed = 0;
        model.RefreshBrowser = () =>
        {
            refreshed++;
            return Task.CompletedTask;
        };
        writer.Write(root, new RegistryValue("First", RegistryValueType.Binary, [1]), null);
        writer.Write(root, new RegistryValue("Second", RegistryValueType.Binary, [2]), null);
        await model.RefreshAsync();
        var first = model.Entries.Single(e => e.Before[0].Scope.ValueNames!.Contains("First"));
        var second = model.Entries.Single(e => e.Before[0].Scope.ValueNames!.Contains("Second"));
        var panel = new object();
        var tab = new object();
        await model.SetSelectionAsync(panel, first);
        await model.SetSelectionAsync(tab, second);
        check(
            !model.RevertCommand.CanExecute(null) && model.RevertCommand.CanExecute(first) && model.RevertCommand.CanExecute(second),
            "One window command accepts each surface's selected entry independently");
        model.ConfirmRevert = _ => Task.FromResult(false);
        await model.RevertCommand.ExecuteAsync(first);
        check(raw.ReadValue(root, "First") is not null && refreshed == 0, "Cancel leaves history and selection targets untouched");

        var bad = Path.Combine(folder, "damaged-fixture.json");
        File.WriteAllText(bad, "{");
        await model.RefreshAsync();
        check(
            model.Entries.Length == 2 && model.Error!.Contains("damaged-fixture.json") && !model.RevertCommand.CanExecute(first),
            "Damaged history preserves readable entries and disables Revert");
        var refused = false;
        try
        {
            journal.Revert(first.Id);
        }
        catch (JsonException)
        {
            refused = true;
        }

        check(
            refused && raw.ReadValue(root, "First") is not null && journal.ReadHistory().Entries.Length == 2,
            "Strict Revert refuses a damaged history before writing a pending entry or changing the registry");
        File.WriteAllText(bad, """{"Before":[null]}""");
        check(journal.ReadHistory().DamagedFiles.Length == 1, "Structurally invalid history also permits inspection");
        File.Delete(bad);
        var firstFile = Path.Combine(folder, $"{first.Id:N}.json");
        var original = File.ReadAllBytes(firstFile);
        try
        {
            EditFile(firstFile, json => json["After"]![0]!["Scope"]!.AsObject().Remove("ValueNames"));
            var mismatched = journal.ReadHistory();
            check(
                mismatched.DamagedFiles.Contains(Path.GetFileName(firstFile)) && mismatched.Entries.Single().Id == second.Id,
                "A record with different before and after target scopes cannot become eligible for Revert");
        }
        finally
        {
            File.WriteAllBytes(firstFile, original);
        }

        await model.RefreshAsync();
        await model.SetSelectionAsync(tab, second);
        check(model.Error is null && model.RevertCommand.CanExecute(second), "Refreshing intact history restores eligibility");
        model.ConfirmRevert = selected => Task.FromResult(selected.Id == second.Id);
        await model.RevertCommand.ExecuteAsync(second);
        check(
            raw.ReadValue(root, "First") is not null
            && raw.ReadValue(root, "Second") is null
            && !model.RevertCommand.CanExecute(second)
            && refreshed == 1, "Revert acts on the captured selection and disables the original");
        var rejected = false;
        model.Notice += (_, _, kind) => rejected |= kind == NoticeKind.Error;
        await model.SetSelectionAsync(panel, first);
        model.ConfirmRevert = entry =>
        {
            new RegistryJournal(folder).Revert(entry.Id);
            return Task.FromResult(true);
        };
        await model.RevertCommand.ExecuteAsync(first);
        check(
            rejected && !model.RevertCommand.CanExecute(first) && journal.ReadEntries().Count(e => e.Reverts == first.Id) == 1,
            "A Revert in another window during confirmation is checked under the writer lock");
        var undoFirst = journal.ReadEntries().Single(e => e.Reverts == first.Id);
        journal.Revert(undoFirst.Id);
        await model.RefreshAsync();
        await model.SetSelectionAsync(panel, first);
        check(
            model.RevertCommand.CanExecute(first) && !model.RevertCommand.CanExecute(undoFirst) && raw.ReadValue(root, "First") is not null,
            "Reverting a Revert restores the original entry's eligibility");
        NoticeKind? notice = null;
        model.Notice += (_, _, kind) => notice = kind;
        model.ConfirmRevert = _ => Task.FromResult(true);
        model.RefreshBrowser = () => Task.FromException(new IOException("Refresh fixture"));
        await model.RevertCommand.ExecuteAsync(first);
        check(
            notice == NoticeKind.Warning && raw.ReadValue(root, "First") is null && !model.RevertCommand.CanExecute(first),
            "A failed browser refresh reports the successful Revert accurately");

        foreach (var legacy in new[] { false, true })
        {
            var oldName = legacy ? "OldLegacy" : "OldTyped";
            var newName = legacy ? "NewLegacy" : "NewTyped";
            raw.CreateKey(root, oldName);
            writer.RenameKey(root.Child(oldName), newName);
            var renamed = journal.ReadEntries().Last();
            EditFile(
                Path.Combine(folder, $"{renamed.Id:N}.json"), json =>
                {
                    if (legacy)
                    {
                        json.Remove("Kind");
                        json["Differences"] = new JsonArray(new JsonObject { ["Untrusted derived content"] = "ignored" });
                    }
                    else
                    {
                        json["Operation"] = "Renamed display text";
                    }
                });
            journal.Revert(renamed.Id);
            check(
                raw.SubKeys(root).Contains(oldName) && !raw.SubKeys(root).Contains(newName),
                legacy
                    ? "Legacy rename records use their original restore behavior"
                    : "Typed rename restore survives a changed display label");
        }

        writer.Write(root, new RegistryValue("Future", RegistryValueType.Binary, [7]), null);
        var future = journal.ReadEntries().Last();
        EditFile(Path.Combine(folder, $"{future.Id:N}.json"), json => json["Kind"] = 999);
        await model.RefreshAsync();
        var unknown = model.Entries.Single(e => e.Id == future.Id);
        check(
            !model.RevertCommand.CanExecute(unknown)
            && unknown.Differences.Count == 1
            && (unknown with { Kind = null, Operation = "Future operation" }).RevertUnavailableReason(new HashSet<Guid>()) is not null
            && (first with { Error = "partial" }).RevertUnavailableReason(new HashSet<Guid>()) is not null
            && (first with { Activity = "external action" }).RevertUnavailableReason(new HashSet<Guid>()) is not null,
            "Unknown, failed and activity records remain inspectable without automatic Revert");
        var unknownBlocked = false;
        try
        {
            journal.Revert(future.Id);
        }
        catch (InvalidOperationException)
        {
            unknownBlocked = true;
        }

        check(unknownBlocked && raw.ReadValue(root, "Future")!.Data[0] == 7, "Core also refuses an unknown operation identifier");
    }

    private static void EditFile(string path, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json);
        File.WriteAllText(path, json.ToJsonString());
    }
}
