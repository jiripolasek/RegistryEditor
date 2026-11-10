// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;


namespace JPSoftworks.Regedit.Tests.Core;

internal static class RegistryKeyDetailsTests
{
    internal static void Run(RegistryService service, RegistryLocation fixture, Action<bool, string> check)
    {
        var branch = fixture.Child("KeyDetails");
        service.CreateKey(fixture, branch.Name);
        try
        {
            var child = branch.Child("Child");
            service.CreateKey(branch, child.Name);
            service.CreateKey(child, "Grandchild");
            service.Write(branch, new RegistryValue(string.Empty, RegistryValueType.Binary, []), null);
            service.Write(
                branch,
                new RegistryValue(
                    "SymbolicLinkValue", RegistryValueType.String,
                    RegistryValue.ParseData(RegistryValueType.String, @"\Registry\Machine\Software")), null);
            var details = service.ReadKeyDetails(branch, true);
            check(
                details is { SubKeyCount: 1, ValueCount: 2, LinkTarget: null },
                "Key details count immediate children and the stored default and ignore a text value named SymbolicLinkValue");
            check(
                details.LastWriteTimeUtc is { } time && time.Offset == TimeSpan.Zero &&
                time > DateTimeOffset.UnixEpoch && time <= DateTimeOffset.UtcNow,
                "Key details expose the registry's UTC last-write time");
            check(
                service.ReadKeyDetails(child.Child("Grandchild"), false) is { SubKeyCount: 0, ValueCount: 0 },
                "An empty key reports zero counts");

            var link = RegistryLocation.Parse(@"HKLM\SYSTEM\CurrentControlSet");
            var source = service.ReadKeyDetails(link, true);
            check(
                source.SubKeyCount == 0 && source.LinkTarget?.StartsWith(
                    @"\Registry\Machine\System\ControlSet", StringComparison.OrdinalIgnoreCase) == true,
                "The local CurrentControlSet link reports its own counts and its native target path");
            check(
                service.ReadKeyDetails(link, false) == source with { LinkTarget = null },
                "Hiding the link target keeps the link's own counts and timestamp");
            var target = RegistryLocation.Parse(@"HKLM\" + source.LinkTarget![@"\Registry\Machine\".Length..]);
            check(
                service.Read(link, true).LastWriteTimeUtc == service.ReadKeyDetails(target, false).LastWriteTimeUtc,
                "Values reached through a registry link use the timestamp of the key that holds those values");
        }
        finally
        {
            service.DeleteKey(branch);
        }
    }
}
