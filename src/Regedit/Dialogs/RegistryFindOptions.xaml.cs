// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class RegistryFindOptions : UserControl
{
    internal FindOptionsViewModel ViewModel { get; }

    internal TextBox Input => this.QueryInput;

    internal RegistryFindOptions(FindOptionsViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
    }
}
