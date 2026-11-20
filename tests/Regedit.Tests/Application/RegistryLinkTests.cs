// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;
using Registry = Microsoft.Win32.Registry;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class RegistryLinkTests
{
    private static RegistryValue Link(string path)
    {
        return new("Alias", RegistryValueType.Link, Encoding.Unicode.GetBytes(path));
    }

    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var source = RegistryLocation.Parse(@"HKCU\Software\LinkSource", RegistryView.Registry32);
        var value = Link($@"\Registry\User\{sid}\Software\LinkTarget");
        var target = RegistryLink.Resolve(value, source)!;
        check(
            target == RegistryLocation.Parse(@"HKCU\Software\LinkTarget", RegistryView.Registry32),
            "Native links to the current user resolve to HKCU and preserve the source registry view");
        check(RegistryLink.DisplayPath(target) == @"HKCU\Software\LinkTarget", "Link target paths use familiar hive aliases");
        var remote = source with { Hive = RegistryHive.Users, Machine = "server" };
        var remoteTarget = RegistryLink.Resolve(value, remote)!;
        check(
            remoteTarget == new RegistryLocation(RegistryHive.Users, sid + @"\Software\LinkTarget", "server", source.View)
            && RegistryLink.DisplayPath(remoteTarget) == $@"\\server\HKU\{sid}\Software\LinkTarget",
            "Remote user targets stay on their source computer and never map to local HKCU");
        var searchRow = new InvestigationRow(new RegistrySearchMatch(remote, value.Name, value, "Data", value.DisplayData));
        check(
            searchRow.MatchEntry?.DataPreview == $@"\\server\HKU\{sid}\Software\LinkTarget"
            && searchRow.MatchEntry.LinkLocation == remoteTarget,
            "Search-row link previews retain the match's remote machine, user hive, SID and registry view");
        check(
            RegistryLink.Resolve(Link(@"\rEgIsTrY\mAcHiNe\Software\Target"), remote)
            == new RegistryLocation(RegistryHive.LocalMachine, @"Software\Target", "server", source.View),
            "Machine link paths resolve case-insensitively and retain the source computer");
        check(
            RegistryLink.Resolve(Link(@"\Registry\User\OtherHive\Key "), source)?.SubKey == @"OtherHive\Key ",
            "Other user hives and significant trailing key whitespace are preserved");
        check(
            RegistryLink.Resolve(Link($@"\Registry\User\{sid}"), source)?.Hive == RegistryHive.CurrentUser
            && RegistryLink.Resolve(Link(@"\Registry\Machine"), source)?.SubKey == string.Empty, "Links can target hive roots");
        check(
            RegistryLink.Resolve(Link(value.DisplayData + '\0'), source) == target, "A stored terminating null does not change the target");
        foreach (var invalid in new[]
                 {
                     Link(string.Empty), Link(@"HKCU\Software"), Link(@"\Registry\Machinery\Software"),
                     Link("\\Registry\\Machine\\bad\0path"), Link(@"\Registry\Machine\bad\\path"),
                     new RegistryValue("Odd", RegistryValueType.Link, [65]),
                     new RegistryValue("Invalid UTF-16", RegistryValueType.Link, [0, 216]),
                     new RegistryValue("Too long", RegistryValueType.Link, new byte[65536]),
                     value with { Type = RegistryValueType.String }
                 })
        {
            check(RegistryLink.Resolve(invalid, source) is null, "Malformed or non-link data does not offer navigation: " + invalid.Name);
        }

        var row = RegistryEntry.ForValue(value, null, source);
        check(
            row.DataPreview == @"HKCU\Software\LinkTarget"
            && row.LinkTarget == row.DataPreview
            && row.Data == value.DisplayData
            && row.Value == value
            && row.Type == "REG_LINK"
            && row.Glyph == "\uE71B",
            "A link row displays its readable target while retaining the original stored type, data and identity");
        var inspector = new ValueInspectorViewModel(row);
        check(
            inspector.LinkTargetPath == row.DataPreview
            && inspector.Data == row.Data
            && inspector.Bytes.SequenceEqual(value.Data)
            && inspector.IsLink
            && inspector.HasText, "Link inspection presents the readable target alongside its original text and raw bytes");
        var invalidInspector = new ValueInspectorViewModel(Link("invalid"));
        await invalidInspector.LoadTargetStatusAsync(new RegistryService());
        check(
            invalidInspector.TargetStatus == "Invalid target path" && !invalidInspector.CanOpenTarget,
            "An invalid link has a clear status without probing the registry");

        var unavailable = new ValueInspectorViewModel(
            RegistryEntry.ForValue(value, null, source with { View = (RegistryView)int.MaxValue }));
        await unavailable.LoadTargetStatusAsync(new RegistryService());
        check(
            unavailable.TargetStatus.StartsWith("Unavailable: ", StringComparison.Ordinal),
            "An unexpected registry argument error completes the target probe with an unavailable status");

        var registry = new RegistryService();
        var browser = new BrowserViewModel(registry, source);
        browser.SetSelection([row]);
        check(
            browser.OpenLinkTargetCommand.CanExecute(null) && browser.ActivateEntryCommand == browser.EditValueCommand,
            "Open target is explicit and ordinary link activation still edits the original value");
        var prompts = new List<RegistryEditPrompt>();
        browser.EditPromptRequested += prompt =>
        {
            prompts.Add(prompt);
            return Task.FromResult(false);
        };
        await browser.EditValueCommand.ExecuteAsync(null);
        await browser.DeleteCommand.ExecuteAsync(null);
        check(
            prompts is [RegistryValueEditorViewModel edit, RegistryDeletePrompt delete]
            && edit.Value == value
            && delete.Items is [StoredRegistryItem.Value stored]
            && stored.Parent == source
            && stored.Original == value, "Edit and delete prompts retain the original REG_LINK value and containing key");
        browser.SetSelection([row, new RegistryEntry(null, value with { Name = "Other" })]);
        check(!browser.OpenLinkTargetCommand.CanExecute(null), "Multiple selection cannot open an ambiguous link target");
        browser.SetSelection([new RegistryEntry(null, Link("invalid"))]);
        check(!browser.OpenLinkTargetCommand.CanExecute(null), "Malformed link paths disable Open target");
        browser.CancelPending();

        if (!registryTests)
        {
            return;
        }

        var fixturePath = @"Software\Regedit.Tests.RegistryLink." + Guid.NewGuid().ToString("N");
        using var fixtureKey = Registry.CurrentUser.CreateSubKey(fixturePath);
        var fixture = RegistryLocation.Parse("HKCU\\" + fixturePath);
        var owner = fixture.Child("Source");
        var destination = fixture.Child("Target");
        var liveLink = Link($@"\Registry\User\{sid}\{destination.SubKey}");
        var sentinel = new RegistryValue("Sentinel", RegistryValueType.DWord, [42, 0, 0, 0]);
        try
        {
            registry.CreateKey(fixture, "Source");
            registry.CreateKey(fixture, "Target");
            registry.Write(owner, liveLink, null);
            registry.Write(destination, sentinel, null);
            var liveRow = RegistryEntry.ForValue(liveLink, null, owner);
            var available = new ValueInspectorViewModel(liveRow);
            await available.LoadTargetStatusAsync(registry);
            check(available.TargetStatus == "Available", "Inspector reports an available link target");
            var missing = new ValueInspectorViewModel(RegistryEntry.ForValue(Link(liveLink.DisplayData + @"\Missing"), null, owner));
            await missing.LoadTargetStatusAsync(registry);
            check(missing.TargetStatus == "Missing", "Inspector distinguishes a missing target");

            using (var permissions = Registry.CurrentUser.OpenSubKey(
                       destination.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree,
                       RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!)
            {
                var original = permissions.GetAccessControl(AccessControlSections.Access);
                var denied = permissions.GetAccessControl(AccessControlSections.Access);
                denied.AddAccessRule(new RegistryAccessRule(identity.User!, RegistryRights.QueryValues, AccessControlType.Deny));
                permissions.SetAccessControl(denied);
                try
                {
                    var inaccessible = new ValueInspectorViewModel(liveRow);
                    await inaccessible.LoadTargetStatusAsync(registry);
                    check(inaccessible.TargetStatus == "Inaccessible", "Inspector distinguishes a target with denied read access");
                }
                finally
                {
                    original.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    permissions.SetAccessControl(original);
                }
            }

            var liveBrowser = new BrowserViewModel(registry, owner);
            await liveBrowser.NavigateAsync(owner);
            liveBrowser.SetSelection([liveBrowser.Entries.Single(entry => entry.Value?.Name == liveLink.Name)]);
            liveBrowser.IsInspectorOpen = true;
            await liveBrowser.PendingPreview;
            var capturedInspector = liveBrowser.Inspector!;
            liveBrowser.SetSelection([new RegistryEntry(null, Link("invalid"))]);
            await capturedInspector.OpenTargetCommand!.ExecuteAsync(null);
            check(liveBrowser.Error is null, "Opening the inspector target succeeds: " + liveBrowser.Error);
            check(
                liveBrowser.Location == destination && liveBrowser.Entries.Any(entry => entry.Value?.SameData(sentinel) == true),
                "Inspector Open target retains its captured target after selection changes and displays target values: "
                + liveBrowser.Location.Address
                + " / "
                + string.Join(", ", liveBrowser.Entries.Select(entry => entry.Name)));
            await liveBrowser.NavigateAsync(owner);
            liveBrowser.SetSelection([liveBrowser.Entries.Single(entry => entry.Value?.Name == liveLink.Name)]);
            await liveBrowser.OpenLinkTargetCommand.ExecuteAsync(null);
            check(liveBrowser.Location == destination, "The list Open target command navigates to the resolved key");
            await liveBrowser.NavigateAsync(owner);
            liveBrowser.SetSelection([liveBrowser.Entries.Single(entry => entry.Value?.Name == liveLink.Name)]);
            liveBrowser.EditPromptRequested += prompt => Task.FromResult(true);
            await liveBrowser.EditValueCommand.ExecuteAsync(null);
            check(
                registry.Read(owner).Values.Single().SameData(liveLink) && registry.Read(destination).Values.Single().SameData(sentinel),
                "Confirming a link edit preserves its REG_LINK bytes and leaves the target value unchanged");
            await liveBrowser.DeleteCommand.ExecuteAsync(null);
            check(
                registry.Read(owner).Values.Length == 0 && registry.Read(destination).Values.Single().SameData(sentinel),
                "Deleting the original link value leaves its target key and values intact");
            liveBrowser.CancelPending();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(fixturePath, false);
        }
    }
}
