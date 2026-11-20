// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Infrastructure.Interop;
using Windows.UI.StartScreen;

namespace JPSoftworks.Regedit.Infrastructure;

internal sealed class WindowsJumpListBackend : IJumpListBackend
{
    public async Task UpdateAsync(JumpListEntry[] entries, Action<IReadOnlySet<string>> saveRemovedArguments)
    {
        if (!JumpListNative.IsPackaged)
        {
            JumpListNative.Update(Environment.ProcessPath!, entries, saveRemovedArguments, JumpListNative.AppId);
            return;
        }

        if (!JumpList.IsSupported())
        {
            return;
        }

        var list = await JumpList.LoadCurrentAsync();
        var removed = list.Items.Where(item => item.RemovedByUser).Select(item => item.Arguments).ToHashSet(StringComparer.Ordinal);
        saveRemovedArguments(removed);
        list.Items.Clear();
        list.SystemGroupKind = JumpListSystemGroupKind.None;
        foreach (var entry in entries.Where(entry => !removed.Contains(entry.Arguments))
                     .DistinctBy(entry => entry.Arguments, StringComparer.Ordinal))
        {
            var item = JumpListItem.CreateWithArguments(entry.Arguments, entry.Title);
            if (entry.FavoriteId is not null)
            {
                item.GroupName = "Favorites";
            }

            list.Items.Add(item);
        }

        try
        {
            await list.SaveAsync();
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005) && list.Items.Any(item => item.GroupName.Length > 0))
        {
            // Match the native backend when Windows privacy settings prohibit custom categories.
            foreach (var item in list.Items.Where(item => item.GroupName.Length > 0).ToArray())
            {
                list.Items.Remove(item);
            }

            await list.SaveAsync();
        }
    }
}
