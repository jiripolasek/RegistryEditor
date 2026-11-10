// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Browser;

internal sealed class RegistryEditRecovery
{
    internal RegistryEditTarget Target { get; }

    internal string Guidance { get; }

    internal bool CanOpenAsAdministrator { get; }

    internal IAsyncRelayCommand OpenAsAdministratorCommand { get; }

    internal RegistryEditRecovery(RegistryEditTarget target, bool administrator, Func<RegistryLocation, Task>? elevate)
    {
        this.Target = target;
        this.CanOpenAsAdministrator = target.Location.Machine is null && !administrator && elevate is not null;
        this.Guidance = administrator
            ?
            "This window is already running as administrator. Check the target's permissions and ownership, or ask its owner for access. Your draft remains here."
            : this.CanOpenAsAdministrator
                ? "You can open a separate administrator window and re-enter the edit there. This window and its draft stay open; the edit is not replayed."
                : "Check the target's permissions or ask its owner for access. Your draft remains here.";
        this.OpenAsAdministratorCommand = new AsyncRelayCommand(
            async () =>
            {
                if (!this.CanOpenAsAdministrator)
                {
                    return;
                }

                try
                {
                    await elevate!(target.Location);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                }
            }, () => this.CanOpenAsAdministrator);
    }
}
