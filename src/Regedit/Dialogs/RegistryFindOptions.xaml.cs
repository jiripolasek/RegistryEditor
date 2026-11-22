// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class RegistryFindOptions : UserControl
{
    internal FindOptionsViewModel ViewModel { get; }

    internal RegistryFindOptions(FindOptionsViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
    }

    internal void FocusInput()
    {
        this.QueryInput.Focus(FocusState.Keyboard);
        this.QueryInput.SelectAll();
    }

    internal void ShowError(string message)
    {
        this.ErrorText.Text = message;
        this.ErrorText.Visibility = Visibility.Visible;
    }
}
