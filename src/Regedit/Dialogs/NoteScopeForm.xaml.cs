// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class NoteScopeForm : UserControl
{
    internal NoteScopeViewModel ViewModel { get; }

    internal Control InitialFocus => this.ScopeInput;

    internal NoteScopeForm(NoteScopeViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
    }
}
