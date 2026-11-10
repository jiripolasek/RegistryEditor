// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Controls.Lists;

public sealed partial class ColumnListView
{
    internal void DismissDetachedEditor(object item, ListColumn column, string text)
    {
        this.CancelEdit();
        var row = new ColumnRow { DataContext = item };
        var box = row.ShowEditor(column, text);
        this._edit = new EditSession(item, column, row, box, column.TextForEdit(item));
        this.ItemsViewChanged(null, null!);
    }
}
