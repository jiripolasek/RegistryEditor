// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class RegistryTargetForm : UserControl
{
    internal RegistryTargetViewModel ViewModel { get; }

    internal Control InitialFocus => this.AddressInput;

    internal RegistryTargetForm(RegistryTargetViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
    }
}
