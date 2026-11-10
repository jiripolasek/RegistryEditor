// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Models.Registry;

namespace JPSoftworks.Regedit.Dialogs;

internal interface IRegistryBranchDialogs
{
    Task<string?> PickSaveAsync(string name, string extension, string description);

    Task<string?> PickOpenAsync(string extension);

    Task<string?> RequestMountNameAsync(string file, string previous, string? error);

    Task<bool> ConfirmAsync(string title, string message, string accept);

    /// <summary>
    /// Asks for a remote computer name. <paramref name="connect"/> runs while the dialog stays open; when it throws,
    /// the error is shown in the dialog and the user can correct the name. Returns false when canceled.
    /// </summary>
    Task<bool> RequestComputerNameAsync(Func<string, Task> connect);

    Task<ReportRange?> ChoosePrintRangeAsync(RegistryLocation location);

    Task PreviewReportAsync(string title, string report, CancellationToken token);

    void ShowPermissions(RegistryLocation target);
}
