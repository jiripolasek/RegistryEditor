// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Security;
using System.Text;
using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Export;

public static class RegistryReport
{
    public static string Create(
        RegistryService service,
        IEnumerable<RegistryLocation> roots,
        bool recursive,
        CancellationToken token = default)
    {
        var report = new StringBuilder();
        foreach (var root in roots)
        {
            var stack = new Stack<RegistryLocation>();
            stack.Push(root);
            while (stack.TryPop(out var location))
            {
                token.ThrowIfCancellationRequested();
                report.AppendLine(location.Address);
                RegistrySnapshot snapshot;
                try
                {
                    snapshot = service.Read(location);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or Win32Exception)
                {
                    report.AppendLine("[Unavailable: " + ex.Message + "]").AppendLine();
                    continue;
                }

                if (snapshot.Values.Length == 0)
                {
                    report.AppendLine("(No values)");
                }

                foreach (var value in snapshot.Values)
                {
                    report.AppendLine($"  {value.DisplayName}  [{value.TypeName}]");
                    report.AppendLine(
                        value.Type is RegistryValueType.String or RegistryValueType.ExpandString or RegistryValueType.Link
                            or RegistryValueType.MultiString
                            ? value.DisplayData
                            : value.EditText(false, true));
                    report.AppendLine();
                }

                report.AppendLine();
                if (recursive)
                {
                    snapshot.PushChildren(stack, location);
                }
            }
        }

        return report.ToString();
    }
}
