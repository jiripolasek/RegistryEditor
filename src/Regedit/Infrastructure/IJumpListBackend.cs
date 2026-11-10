// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Infrastructure;

internal interface IJumpListBackend
{
    Task UpdateAsync(JumpListEntry[] entries, Action<IReadOnlySet<string>> saveRemovedArguments);
}
