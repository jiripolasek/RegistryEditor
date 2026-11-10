// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace JPSoftworks.Regedit.Core.Journal;

internal sealed partial class AdministratorJournalDirectory : IDisposable
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);

    private static readonly SecurityIdentifier TrustedInstaller
        = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private readonly List<SafeFileHandle> _handles = [];
    private const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;

    public static AdministratorJournalDirectory Open(string folder)
    {
        var data = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar);
        folder = Path.GetFullPath(folder);
        if (!folder.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw UnsafeStore();
        }

        var lease = new AdministratorJournalDirectory();
        try
        {
            var directories = new Stack<DirectoryInfo>();
            for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
            {
                directories.Push(directory);
            }

            foreach (var directory in directories)
            {
                var isPrivate = directory.FullName.StartsWith(
                    data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (isPrivate)
                {
                    directory.Create(DirectoryDescriptor());
                }

                // Keep every component in place and prevent reparse writes until the operation finishes.
                var handle = OpenDirectory(directory.FullName, 0x00020080, FileShare.Read, 0, 3, 0x02200000, 0);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw new IOException(
                        "The administrator journal directory could not be secured.", new Win32Exception(error));
                }

                lease._handles.Add(handle);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw UnsafeStore();
                }

                var security = directory.GetAccessControl(Sections);
                if (isPrivate)
                {
                    ValidatePrivate(security, true);
                }
                else
                {
                    ValidateParent(security);
                }
            }

            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public static void ValidateFile(string file)
    {
        var info = new FileInfo(file);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw UnsafeStore();
        }

        ValidatePrivate(info.GetAccessControl(Sections), false);
    }

    internal static void ValidateFile(FileStream stream)
    {
        if ((File.GetAttributes(stream.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
        {
            throw UnsafeStore();
        }

        ValidatePrivate(stream.GetAccessControl(), false);
    }

    public static FileStream OpenFile(string file, FileMode mode)
    {
        if (File.Exists(file))
        {
            ValidateFile(file);
        }

        var security = new FileSecurity();
        SetPrivateAccess(security, InheritanceFlags.None);
        return new FileInfo(file).Create(
            mode, FileSystemRights.Read | FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
    }

    private static DirectorySecurity DirectoryDescriptor()
    {
        var security = new DirectorySecurity();
        SetPrivateAccess(security, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        return security;
    }

    private static void SetPrivateAccess(FileSystemSecurity security, InheritanceFlags inheritance)
    {
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, SystemAccount })
        {
            security.AddAccessRule(
                new FileSystemAccessRule(
                    sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
    }

    private static AuthorizationRuleCollection Rules(FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (descriptor.DiscretionaryAcl is null || descriptor.DiscretionaryAcl.Count != rules.Count)
        {
            throw UnsafeStore();
        }

        return rules;
    }

    private static bool IsAdministrator(IdentityReference? sid)
    {
        return Administrators.Equals(sid) || SystemAccount.Equals(sid);
    }

    private static void ValidatePrivate(FileSystemSecurity security, bool directory)
    {
        if (!IsAdministrator(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
        {
            throw UnsafeStore();
        }

        var rules = Rules(security);
        var inheritance = directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        if (rules.Count != 2)
        {
            throw UnsafeStore();
        }

        var identities = new HashSet<IdentityReference>();
        foreach (FileSystemAccessRule rule in rules)
        {
            if (!IsAdministrator(rule.IdentityReference) || !identities.Add(rule.IdentityReference) ||
                rule.AccessControlType != AccessControlType.Allow ||
                rule.FileSystemRights != FileSystemRights.FullControl || rule.InheritanceFlags != inheritance ||
                rule.PropagationFlags != PropagationFlags.None || rule.IsInherited)
            {
                throw UnsafeStore();
            }
        }
    }

    private static void ValidateParent(DirectorySecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!IsAdministrator(owner) && !TrustedInstaller.Equals(owner))
        {
            throw UnsafeStore();
        }

        foreach (FileSystemAccessRule rule in Rules(security))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                !IsAdministrator(rule.IdentityReference) && !TrustedInstaller.Equals(rule.IdentityReference) &&
                (rule.FileSystemRights & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                                          FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
                                          (FileSystemRights)0x10000000)) != 0)
            {
                throw UnsafeStore();
            }
        }
    }

    private static IOException UnsafeStore()
    {
        return new IOException(
            "The administrator journal has unsafe ownership, permissions, or a redirected path. Registry edits require a secure journal. Repair or move the journal using an administrator account; it cannot fall back to the user profile.");
    }

    public void Dispose()
    {
        for (var index = this._handles.Count - 1; index >= 0; index--)
        {
            this._handles[index].Dispose();
        }
    }

    [LibraryImport(
        "kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenDirectory(
        string path,
        uint access,
        FileShare share,
        nint security,
        uint disposition,
        uint flags,
        nint template);
}
