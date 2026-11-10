// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Journal;

// Numeric identifiers are persisted in journal files.
public enum JournalOperation
{
    Unknown = 0, CreateKey = 1, RenameKey = 2, DeleteKey = 3, CopyKey = 4,
    CreateValue = 5, EditValue = 6, RenameValue = 7, DeleteValue = 8, Revert = 9, Activity = 10
}

internal static class JournalOperations
{
    internal static string Label(this JournalOperation operation)
    {
        return operation switch
        {
            JournalOperation.CreateKey => "Create key",
            JournalOperation.RenameKey => "Rename key",
            JournalOperation.DeleteKey => "Delete key",
            JournalOperation.CopyKey => "Copy key",
            JournalOperation.CreateValue => "Create value",
            JournalOperation.EditValue => "Edit value",
            JournalOperation.RenameValue => "Rename value",
            JournalOperation.DeleteValue => "Delete value",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    internal static JournalOperation FromLegacy(string operation)
    {
        return operation switch
        {
            "Create key" => JournalOperation.CreateKey,
            "Rename key" => JournalOperation.RenameKey,
            "Delete key" => JournalOperation.DeleteKey,
            "Copy key" => JournalOperation.CopyKey,
            "Create value" => JournalOperation.CreateValue,
            "Edit value" => JournalOperation.EditValue,
            "Rename value" => JournalOperation.RenameValue,
            "Delete value" => JournalOperation.DeleteValue,
            _ => JournalOperation.Unknown
        };
    }
}
