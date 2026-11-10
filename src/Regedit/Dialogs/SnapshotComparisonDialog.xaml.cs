// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class SnapshotComparisonDialog : ContentDialog
{
    internal SnapshotComparisonDialog(SnapshotComparisonView content)
    {
        this.InitializeComponent();
        this.ComparisonHost.Content = content;
    }
}
