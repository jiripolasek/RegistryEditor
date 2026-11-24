// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Workspaces;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Views.Pages;

public sealed partial class BrowserPage
{
    private void UpdateValueInspector()
    {
        var action = this.ViewModel.IsInspectorOpen ? "Close detail panel" : "Show detail panel";
        AutomationProperties.SetName(this.ToggleValueInspector, action);
        ToolTipService.SetToolTip(this.ToggleValueInspector, action + " (Alt+P)");
        this.ValueInspectorButtonIcon.Glyph = this.ViewModel.IsInspectorOpen ? "\uE89F" : "\uE8A0";
        if (!this.ViewModel.IsInspectorOpen)
        {
            this.ValueInspectorContent.Content = null;
        }
        else if (this.ViewModel.Inspector is { } inspector)
        {
            var content = new ValueInspector(inspector, this._window.Dialogs);
            this.ConfigureInspectorNotes(content);
            this.ValueInspectorContent.Content = content;
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

    private KeyNote? ValueNoteTarget(RegistryEntry entry)
    {
        return entry.IsKey || entry.IsParent || this.ViewModel.Location.IsComputer
            ? null
            : new KeyNote(this.ViewModel.Location, string.Empty) { ValueName = entry.Value?.Name ?? string.Empty };
    }

    private void EntryValueNote_Click(object sender, RoutedEventArgs args)
    {
        if (this.ViewModel.SelectedEntries is [var entry] && this.ValueNoteTarget(entry) is { } target)
        {
            this._window.ShowNote(this._window.ViewModel.NoteStore.Get(target));
        }
    }

    private void OpenNoteMarker(RegistryEntry entry)
    {
        if (entry.SavedNote is { } note)
        {
            this._window.ShowNote(this._window.ViewModel.NoteStore.Get(note));
        }
    }

    private void ConfigureInspectorNotes(ValueInspector inspector, ContentDialog? dialog = null)
    {
        var location = inspector.ViewModel.KeyLocation ?? this.ViewModel.Location;
        if (location.IsComputer || (dialog is not null && inspector.ViewModel.HasNavigationIcon))
        {
            return;
        }

        inspector.Notes = new InspectorNotesViewModel(
            this._window.ViewModel.NoteStore, location, inspector.ViewModel.HasNavigationIcon,
            inspector.ViewModel.InspectedValue, showContainingKeyNote: dialog is null, open: note =>
            {
                dialog?.Hide();
                this._window.ShowNote(note);
            });
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

    private Task ShowValueInspectorAsync(ValueInspectorViewModel inspector)
    {
        return ValueInspector.ShowDialogAsync(inspector, this._window.Dialogs, this.ConfigureInspectorNotes);
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
