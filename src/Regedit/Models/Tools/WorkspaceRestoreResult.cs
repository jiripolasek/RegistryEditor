// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Tools;

internal sealed record WorkspaceRestoreResult(int Tabs, int Searches, int Watches, bool Replaced, string[] Errors);
