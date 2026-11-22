// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class RegistryFindOptions : UserControl
{
    internal FindOptionsViewModel ViewModel { get; }

    internal RegistryFindOptions(FindOptionsViewModel viewModel)
    {
        this.ViewModel = viewModel;
        this.InitializeComponent();
        foreach (var picker in new[] { this.ModifiedFromPicker, this.ModifiedThroughPicker })
        {
            picker.MinDate = DateTimeOffset.FromFileTime(0);
            picker.MaxDate = DateTimeOffset.MaxValue;
        }
    }

    internal void FocusInput()
    {
        this.QueryInput.Focus(FocusState.Keyboard);
        this.QueryInput.SelectAll();
        if (this.ViewModel.InitialError is { } error)
        {
            this.ShowError(error);
        }
    }

    internal void ShowError(string message)
    {
        this.ErrorText.Text = message;
        this.ErrorText.Visibility = Visibility.Visible;
        this.ErrorText.StartBringIntoView();
        FrameworkElementAutomationPeer.CreatePeerForElement(this.ErrorText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void Field_AccessKeyInvoked(UIElement sender, AccessKeyInvokedEventArgs args)
    {
        args.Handled = ((Control)sender).Focus(FocusState.Keyboard);
    }

    private void ClearModifiedDates_Click(object sender, RoutedEventArgs args)
    {
        this.ModifiedFromPicker.Date = this.ModifiedThroughPicker.Date = null;
        this.ModifiedFromPicker.Focus(FocusState.Keyboard);
    }
}
