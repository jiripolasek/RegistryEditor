// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.Input;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class BrowserPage
{
    private bool _isInterpretationExpanded;

    private void UpdateValueInspector()
    {
        if (this.ValueInspectorContent.Content is ValueInspector previous)
        {
            this._isInterpretationExpanded = previous.IsInterpretationExpanded;
        }

        var action = this.ViewModel.IsInspectorOpen ? "Close detail panel" : "Show detail panel";
        AutomationProperties.SetName(this.ToggleValueInspector, action);
        ToolTipService.SetToolTip(this.ToggleValueInspector, action + " (Alt+P)");
        this.ValueInspectorButtonIcon.Glyph = this.ViewModel.IsInspectorOpen ? "\uE89F" : "\uE8A0";
        if (!this.ViewModel.IsInspectorOpen)
        {
            this.ValueInspectorContent.Content = null;
            this._isInterpretationExpanded = false;
        }
        else if (this.ViewModel.Inspector is { } inspector)
        {
            var location = inspector.KeyLocation ?? this.ViewModel.Location;
            this.ValueInspectorContent.Content = new ValueInspector(
                inspector, this._window.Dialogs, location.IsComputer ? null : this._window.ViewModel.GetNote(location),
                new RelayCommand(() => this._window.ShowNotesForKey(location)))
            {
                IsInterpretationExpanded = this._isInterpretationExpanded
            };
        }
        else
        {
            this.ValueInspectorContent.Content = new TextBlock
            {
                Text = "Select one key or value to inspect.", TextWrapping = TextWrapping.Wrap
            };
        }

        this.ValueInspectorScroll.IsTabStop = this.ViewModel.Inspector is null;
        this.ArrangeValueInspector();
    }

    private void ArrangeValueInspector()
    {
        var visible = this.ViewModel.IsInspectorOpen;
        var beside = this.ValueWorkspace.ActualWidth >= 760;
        this.ValueInspectorPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        this.ValueInspectorHeader.Visibility = beside ? Visibility.Collapsed : Visibility.Visible;
        this.ValueInspectorWidth.Width = visible && beside ? new GridLength(340) : new GridLength(0);
        this.ValueInspectorHeight.Height = visible && !beside ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(this.ValueInspectorPane, beside ? 1 : 0);
        Grid.SetRow(this.ValueInspectorPane, beside ? 0 : 1);
        Grid.SetColumnSpan(this.ValueInspectorPane, beside ? 1 : 2);
        this.ValueInspectorPane.BorderThickness = beside ? new Thickness(1, 1, 0, 1) : new Thickness(0, 1, 0, 1);
        this.ValueInspectorSplitter.Arrange(
            this.ValueInspectorPane, this.ValueInspectorWidth, this.ValueInspectorHeight, beside, visible,
            () => this.ValueWorkspace.ActualWidth);
    }

    private void ValueWorkspace_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.ArrangeValueInspector();
    }

    private async Task ShowValueInspectorAsync(ValueInspectorViewModel inspector)
    {
        await this._window.Dialogs.ShowAsync(new AppDialog("Value inspector", new ValueInspector(inspector), "Close", string.Empty));
    }

    private void ToggleValueInspector_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.IsInspectorOpen = !this.ViewModel.IsInspectorOpen;
        this.EntryList.FocusList(FocusState.Keyboard);
    }

    private void CloseValueInspector_Click(object sender, RoutedEventArgs args)
    {
        this.ViewModel.IsInspectorOpen = false;
        this.EntryList.FocusList(FocusState.Keyboard);
    }
}
