// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Registry;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class PrintRangeForm : UserControl
{
    internal ReportRange Range => (ReportRange)this.RangeInput.SelectedIndex;

    internal RadioButtons Input => this.RangeInput;

    internal PrintRangeForm(bool computer)
    {
        this.InitializeComponent();
        this.RangeInput.SelectedIndex = computer ? 2 : 0;
        this.RangeInput.IsEnabled = !computer;
    }
}
