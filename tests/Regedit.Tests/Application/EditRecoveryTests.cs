// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using JPSoftworks.Regedit.Core.Journal;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class EditRecoveryTests
{
    internal static async Task RunAsync(Action<bool, string> check, bool registryTests)
    {
        var probes = 0;

        string[] Siblings()
        {
            probes++;
            return ["Source", "tAkEn"];
        }

        check(
            RegistryService.RenameCollides(183, "Source", "Taken", Siblings) && probes == 0,
            "Native rename error 183 is a definite collision without a sibling probe");
        check(
            RegistryService.RenameCollides(5, "Source", "TAKEN", Siblings) && probes == 1,
            "Native rename access denial is a collision only for a confirmed case-insensitive sibling");
        check(
            !RegistryService.RenameCollides(5, "Source", "sOuRcE", Siblings)
            && probes == 1
            && !RegistryService.RenameCollides(2, "Source", "Taken", Siblings),
            "Case-only rename excludes the source and unrelated native errors are not reclassified");
        foreach (var failure in new Exception[]
                 {
                     new UnauthorizedAccessException(), new IOException(), new SecurityException(), new Win32Exception(5)
                 })
        {
            check(
                !RegistryService.RenameCollides(5, "Source", "Taken", () => throw failure),
                "A failed sibling probe preserves the original native access denial: " + failure.GetType().Name);
        }

        var location = RegistryLocation.Parse(@"HKCU\Software\Captured target ", RegistryView.Registry32);
        var target = new RegistryEditTarget(location, "OriginalValue");
        var prompt = new RegistryNamePrompt(RegistryNameOperation.Rename, "typed retry name");
        var denial = new Win32Exception(5);
        var launches = new List<ProcessStartInfo>();

        Task Elevate(RegistryLocation captured)
        {
            launches.Add(RegistryLaunch.CreateElevationStartInfo("regedit2.exe", captured));
            return Task.CompletedTask;
        }

        prompt.SetFailure(denial, target, false, Elevate);
        check(
            ReferenceEquals(prompt.Failure, denial)
            && prompt.Error == denial.Message
            && launches.Count == 0
            && prompt.Recovery?.Target == target
            && prompt.Recovery.CanOpenAsAdministrator,
            "Access recovery retains the exact failure target view and draft without automatic elevation");
        await prompt.Recovery!.OpenAsAdministratorCommand.ExecuteAsync(null);
        check(
            launches.Count == 1
            && launches[0].ArgumentList.SequenceEqual([location.Address, "--32"])
            && prompt.Name == "typed retry name"
            && ReferenceEquals(prompt.Failure, denial),
            "Only the explicit recovery command requests runas and it transmits no editor draft");
        prompt.SetFailure(denial, target, false, _ => Task.FromException(new Win32Exception(1223)));
        await prompt.Recovery!.OpenAsAdministratorCommand.ExecuteAsync(null);
        check(
            prompt.Name == "typed retry name" && ReferenceEquals(prompt.Failure, denial),
            "Canceling an administrator launch preserves the original draft and access failure");
        prompt.SetFailure(denial, target, true, Elevate);
        check(
            !prompt.Recovery!.CanOpenAsAdministrator && prompt.Recovery.Guidance.Contains("already running as administrator"),
            "An elevated denial offers permission recovery without another elevation action");
        await prompt.Recovery.OpenAsAdministratorCommand.ExecuteAsync(null);
        check(launches.Count == 1, "Executing disabled elevated recovery cannot launch another window");
        prompt.SetFailure(denial, new RegistryEditTarget(RegistryLocation.Parse(@"\\fixture\HKLM\Software")), false, Elevate);
        check(!prompt.Recovery!.CanOpenAsAdministrator, "Local elevation recovery is not offered for a remote target");
        foreach (var failure in new Exception[]
                 {
                     new IOException("Before-state capture failed", denial),
                     new IOException("The final journal state could not be saved", new UnauthorizedAccessException()),
                     new IOException("A key with this name already exists.", denial)
                 })
        {
            prompt.SetFailure(failure, target, false, Elevate);
            check(
                prompt.Recovery is null && ReferenceEquals(prompt.Failure, failure) && prompt.Error == failure.Message,
                "Capture integrity and collision errors retain their identity without permission reclassification");
        }

        var blockedJournal = Directory.CreateTempSubdirectory("Regedit.JournalDenied.");
        try
        {
            Directory.CreateDirectory(Path.Combine(blockedJournal.FullName, "write.lock"));
            IOException? storageFailure = null;
            var changed = false;
            try
            {
                new RegistryJournal(blockedJournal.FullName).RecordActivity("Denied storage", location, "test", () => changed = true);
            }
            catch (IOException ex)
            {
                storageFailure = ex;
            }

            if (storageFailure is null)
            {
                throw new InvalidOperationException("Journal storage denial did not fail.");
            }

            prompt.SetFailure(storageFailure, target, false, Elevate);
            check(
                !changed
                && storageFailure.InnerException is UnauthorizedAccessException
                && storageFailure.Message.Contains("journal storage")
                && prompt.Recovery is null
                && launches.Count == 1
                && Directory.GetFiles(blockedJournal.FullName, "*.json").Length == 0,
                "A journal file access denial preserves its storage error without mutation or registry elevation recovery");
        }
        finally
        {
            blockedJournal.Delete(true);
        }

        if (!registryTests)
        {
            return;
        }

        var service = new RegistryService();
        var software = RegistryLocation.Parse(@"HKCU\Software", RegistryView.Registry32);
        var fixture = software.Child("RegeditEditRecovery." + Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateTempSubdirectory("RegeditEditRecovery.");
        service.CreateKey(software, fixture.Name);
        try
        {
            var source = fixture.Child("DeniedSource");
            var collisionSource = fixture.Child("CollisionSource");
            var existing = fixture.Child("Existing");
            var other = fixture.Child("Other");
            var elsewhere = fixture.Child("Elsewhere") with { View = RegistryView.Registry64 };
            foreach (var child in new[] { source, collisionSource, existing, other, elsewhere })
            {
                service.CreateKey(fixture, child.Name);
            }

            var original = new RegistryValue(
                "Value", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "original data"));
            service.Write(source, original, null);
            service.Write(collisionSource, original, null);
            service.Write(
                existing,
                new RegistryValue("Value", RegistryValueType.String, RegistryValue.ParseData(RegistryValueType.String, "existing data")),
                null);
            service.Journal = new RegistryJournal(folder.FullName);
            var browser = new BrowserViewModel(service, other);
            await browser.NavigateAsync(other);
            var collisionPrompts = 0;
            browser.EditPromptRequested += form =>
            {
                var rename = (RegistryNamePrompt)form;
                collisionPrompts++;
                if (collisionPrompts == 1)
                {
                    rename.Name = "eXiStInG";
                }
                else
                {
                    check(
                        rename.Name == "eXiStInG"
                        && form.Failure is IOException { InnerException: Win32Exception native }
                        && native.NativeErrorCode is 5 or 183
                        && form.Recovery is null
                        && service.ReadValue(collisionSource, "Value")!.SameData(original)
                        && service.ReadValue(existing, "Value")!.DisplayData == "existing data",
                        "A real key collision retains the attempted spelling and both keys without offering elevation");
                    rename.Name = "FreeName";
                }

                return Task.FromResult(true);
            };
            await browser.RenameTreeKeyCommand.ExecuteAsync(collisionSource);
            check(
                collisionPrompts == 2 && service.ReadValue(fixture.Child("FreeName"), "Value")!.SameData(original),
                "A key collision retries successfully with the corrected name and exact original contents");
            browser.CancelPending();

            using var registryRoot = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, source.View);
            using var permissions = registryRoot.OpenSubKey(
                source.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadKey | RegistryRights.ChangePermissions)!;
            var originalSecurity = permissions.GetAccessControl(AccessControlSections.Access);
            var originalDacl = originalSecurity.GetSecurityDescriptorBinaryForm();
            var originalAces = new RawSecurityDescriptor(originalDacl, 0).DiscretionaryAcl!;

            void RestorePermissions()
            {
                var restored = new RegistrySecurity();
                restored.SetSecurityDescriptorBinaryForm(originalDacl, AccessControlSections.Access);
                permissions.SetAccessControl(restored);
            }

            void VerifyRestoredAccess()
            {
                var restored = permissions.GetAccessControl(AccessControlSections.Access);
                var aces = new RawSecurityDescriptor(restored.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl;
                using var writable = registryRoot.OpenSubKey(
                    source.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.Delete | RegistryRights.SetValue);
                check(
                    writable is not null
                    && restored.AreAccessRulesProtected == originalSecurity.AreAccessRulesProtected
                    && aces is not null
                    && aces.Cast<GenericAce>().SequenceEqual(originalAces.Cast<GenericAce>()),
                    "The fixture restores the original access rules and regains delete and value-write rights");
            }

            var user = WindowsIdentity.GetCurrent().User!;
            var deniedSecurity = permissions.GetAccessControl(AccessControlSections.Access);
            deniedSecurity.AddAccessRule(new RegistryAccessRule(user, RegistryRights.Delete, AccessControlType.Deny));
            try
            {
                permissions.SetAccessControl(deniedSecurity);
                browser = new BrowserViewModel(service, other);
                await browser.NavigateAsync(other);
                var requests = new List<ProcessStartInfo>();
                browser.ElevationRequested += captured =>
                {
                    requests.Add(RegistryLaunch.CreateElevationStartInfo("regedit2.exe", captured));
                    return Task.CompletedTask;
                };
                Exception? actualFailure = null;
                browser.EditFailed += error => actualFailure = error;
                var deniedPrompts = 0;
                browser.EditPromptRequested += async form =>
                {
                    var rename = (RegistryNamePrompt)form;
                    if (++deniedPrompts == 1)
                    {
                        rename.Name = "DeniedRename";
                        return true;
                    }

                    check(
                        rename.Name == "DeniedRename"
                        && ReferenceEquals(form.Failure, actualFailure)
                        && form.Failure is Win32Exception { NativeErrorCode: 5 }
                        && form.Recovery?.Target.Location == source
                        && requests.Count == 0
                        && service.SubKeys(fixture).Contains(source.Name)
                        && !service.SubKeys(fixture).Contains(rename.Name),
                        "Genuine ACL rename denial preserves native error 5 the typed name and exact tree target without mutation");
                    await browser.NavigateAsync(elsewhere);
                    await form.Recovery!.OpenAsAdministratorCommand.ExecuteAsync(null);
                    check(
                        requests.Single().ArgumentList.SequenceEqual([source.Address, "--32"]) && browser.Location == elsewhere,
                        "Explicit recovery keeps its captured tree target and view after independent browser navigation");
                    return false;
                };
                await browser.RenameTreeKeyCommand.ExecuteAsync(source);
                check(
                    deniedPrompts == 2
                    && browser.Location == elsewhere
                    && service.ReadValue(source, "Value")!.SameData(original)
                    && !service.SubKeys(fixture).Contains("DeniedRename"),
                    "Canceling the denied edit leaves the original window location and registry contents intact");
                browser.CancelPending();
            }
            finally
            {
                RestorePermissions();
            }

            VerifyRestoredAccess();
            deniedSecurity = permissions.GetAccessControl(AccessControlSections.Access);
            deniedSecurity.AddAccessRule(new RegistryAccessRule(user, RegistryRights.SetValue, AccessControlType.Deny));
            try
            {
                permissions.SetAccessControl(deniedSecurity);
                browser = new BrowserViewModel(service, source);
                await browser.NavigateAsync(source);
                browser.SelectedEntry = browser.Entries.Single(row => row.Value?.Name == "Value");
                var dataPrompts = 0;
                RegistryValueEditorViewModel? draft = null;
                Exception? valueFailure = null;
                var retainedValue = false;
                browser.EditPromptRequested += async form =>
                {
                    draft = (RegistryValueEditorViewModel)form;
                    if (++dataPrompts == 1)
                    {
                        draft.Data = "retained typed data";
                    }
                    else
                    {
                        valueFailure = draft.Failure;
                        retainedValue = draft.Data == "retained typed data"
                                        && draft.Name == "Value"
                                        && draft.Recovery?.Target == new RegistryEditTarget(source, "Value")
                                        && service.ReadValue(source, "Value")!.SameData(original);
                        RestorePermissions();
                        VerifyRestoredAccess();
                        await browser.NavigateAsync(elsewhere);
                    }

                    return true;
                };
                await browser.EditValueCommand.ExecuteAsync(null);
                check(
                    retainedValue && valueFailure is SecurityException,
                    "A genuine value ACL denial retains direct SecurityException typed data name target and view without writing");
                check(
                    dataPrompts == 2
                    && service.ReadValue(source, "Value")!.DisplayData == "retained typed data"
                    && service.ReadValue(elsewhere, "Value") is null
                    && browser.Location == elsewhere
                    && draft!.Recovery is null,
                    "Permission recovery retries the retained data at its original target while preserving later navigation");
                browser.CancelPending();
            }
            finally
            {
                RestorePermissions();
            }
        }
        finally
        {
            service.Journal = null;
            try
            {
                service.DeleteKey(fixture);
            }
            finally
            {
                folder.Delete(true);
            }
        }
    }
}
