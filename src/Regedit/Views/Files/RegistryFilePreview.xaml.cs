// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using System.ComponentModel;
using JPSoftworks.Notifications;
using JPSoftworks.Regedit.Services;
using JPSoftworks.Regedit.Models.Tools;
using System.Windows.Input;
using JPSoftworks.Regedit.Controls.Lists;
using JPSoftworks.Regedit.Core.Import;
using JPSoftworks.Regedit.Models.Files;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.ViewModels.Files;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.UI.Core;
using WinUIEditor;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace JPSoftworks.Regedit.Views.Files;

/// <summary>WinUIEdit integration and read-only tree/list presentation for a registry file view model.</summary>
public sealed partial class RegistryFilePreview : UserControl
{
    private readonly Dictionary<string, TreeViewNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TreeViewNode, string> _paths = [];
    private readonly ObservableCollection<object> _selectedValues = [];

    private readonly NotificationSession _notifications = new(
        TimeSpan.FromSeconds(new Windows.UI.ViewManagement.UISettings().MessageDuration));
    private RegistryFont? _editorFont;
    private bool _loadingSource, _synchronizingSource;
    private Control? _previousFocus;
    private GridLength _splitSourceWidth = new(1, GridUnitType.Star);
    private TreeViewNode? _synchronizedNode;

    internal RegistryFilePreviewViewModel ViewModel { get; private set; } = null!;

    // The XAML enum parser only accepts named keys, and VirtualKey does not name comma.
    private VirtualKey CommaKey => (VirtualKey)188;

    public RegistryFilePreview()
    {
        this.InitializeComponent();
        this.NotificationOverlay.Attach(this._notifications);
        this.NotificationOverlay.ActionFailed += error => this.ViewModel.ReportFailure(error);
        this.LayoutModes.SelectionChanged += this.Layout_SelectionChanged;
        this.SourceEditor.Editor.Modified += this.Source_Modified;
        this.SourceEditor.Editor.UpdateUI += this.Source_UpdateUI;
        this.SourceEditor.Editor.ModEventMask = (ModificationFlags)3;
        this.SourceEditor.Editor.SetMarginWidthN(0, 48);
        this.InitializeHighlighting();
        this.InitializeDiagnostics();
        this.Values.Columns.Add(
            new ListColumn
            {
                Id = "name",
                Header = "Name",
                DefaultWidth = 140,
                MinWidth = 120,
                CellTemplate = (DataTemplate)this.Resources["FileNameCell"],
                CellText = row => (row as RegistryFilePreviewRow)?.Name
            });
        this.Values.Columns.Add(
            new ListColumn
            {
                Id = "operation",
                Header = "Operation",
                DefaultWidth = 110,
                CellText = row => (row as RegistryFilePreviewRow)?.Operation
            });
        this.Values.Columns.Add(
            new ListColumn
            {
                Id = "type",
                Header = "Type",
                DefaultWidth = 100,
                CellText = row => (row as RegistryFilePreviewRow)?.Type
            });
        this.Values.Columns.Add(
            new ListColumn
            {
                Id = "data",
                Header = "Data",
                MinWidth = 120,
                CellText = row => (row as RegistryFilePreviewRow)?.Data
            });
        this.Values.Columns.Add(
            new ListColumn
            {
                Id = "line",
                Header = "Line",
                DefaultWidth = 48,
                CellText = row => (row as RegistryFilePreviewRow)?.Line.ToString()
            });
        this.Loaded += (_, _) => this.DispatcherQueue.TryEnqueue(this.ApplyEditorSurface);
        this.SourceEditor.SyntaxHighlightingApplied
            += (_, _) => this.DispatcherQueue.TryEnqueue(this.ApplyEditorSurface);
        this.Workspace.SizeChanged += (_, _) =>
            this.MetadataContent.MaxHeight = Math.Clamp(this.Workspace.ActualHeight - 336, 48, 180);
        this.Values.SelectedItems = this._selectedValues;
        this._selectedValues.CollectionChanged += (_, _) =>
        {
            if (this.ViewModel is { } model)
            {
                model.SelectedValue = this._selectedValues.FirstOrDefault() as RegistryFilePreviewRow;
            }
        };
    }

    internal JPSoftworks.Notifications.WinUI.NotificationHost NotificationHost => this.NotificationOverlay;

    internal void Initialize(RegistryFilePreviewViewModel model, RegistryFont font)
    {
        this.ViewModel = model;
        model.PropertyChanged += this.Model_Changed;
        model.Notice += this.ShowNotice;
        model.SourceLoaded += this.LoadEditorSource;
        model.Saved += this.SetEditorSavePoint;
        model.EditHistoryRequested += this.EditHistory;
        model.SourceLineRequested += this.GoToSourceLine;
        // Segmented remembers its first selection when the template loads.
        this.LayoutModes.SelectedIndex = (int)model.Layout;
        this.Bindings.Update();
        this.ApplyEditorFont(font);
        this.LoadEditorSource();
        this.RenderDocument();
        this.UpdateDiagnosticMarkers();
        this.UpdateInteraction();
    }

    internal void Detach()
    {
        this.ViewModel.PropertyChanged -= this.Model_Changed;
        this.ViewModel.Notice -= this.ShowNotice;
        this.NotificationOverlay.Dispose();
        this._notifications.Dispose();
        this.ViewModel.SourceLoaded -= this.LoadEditorSource;
        this.ViewModel.Saved -= this.SetEditorSavePoint;
        this.ViewModel.EditHistoryRequested -= this.EditHistory;
        this.ViewModel.SourceLineRequested -= this.GoToSourceLine;
        this.SourceEditor.Editor.Modified -= this.Source_Modified;
        this.SourceEditor.Editor.UpdateUI -= this.Source_UpdateUI;
        this.SourceEditor.Editor.MarginClick -= this.Source_MarginClick;
    }

    private void Model_Changed(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(RegistryFilePreviewViewModel.Text) when !this._synchronizingSource:
                this.SetEditorText();
                break;
            case nameof(RegistryFilePreviewViewModel.Document):
                this.RenderDocument();
                this.UpdateInteraction();
                break;
            case nameof(RegistryFilePreviewViewModel.Rows):
                this._selectedValues.Clear();
                this.Values.ItemsSource = this.ViewModel.Rows;
                break;
            case nameof(RegistryFilePreviewViewModel.SelectedKeyPath):
                this.SelectNode(this.ViewModel.SelectedKeyPath);
                break;
            case nameof(RegistryFilePreviewViewModel.Metadata):
                this.UpdateMetadata(this.ViewModel.Metadata);
                break;
            case nameof(RegistryFilePreviewViewModel.Layout):
                this.LayoutModes.SelectedIndex = (int)this.ViewModel.Layout;
                this.ApplyLayout();
                break;
            case nameof(RegistryFilePreviewViewModel.IsBusy):
                if (this.ViewModel.IsBusy)
                {
                    AccessKeyManager.ExitDisplayMode();
                    this._previousFocus = this.XamlRoot is { } root
                        ? FocusManager.GetFocusedElement(root) as Control
                        : null;
                }

                this.UpdateInteraction();
                this.RestoreOperationFocus();
                break;
            case nameof(RegistryFilePreviewViewModel.IsClosePending):
                this.UpdateInteraction();
                this.RestoreOperationFocus();
                break;
            case nameof(RegistryFilePreviewViewModel.IsClosed):
                this.UpdateInteraction();
                break;
            case nameof(RegistryFilePreviewViewModel.Diagnostics):
                this.UpdateDiagnosticMarkers();
                break;
        }
    }

    private void ShowNotice(string title, string message, NoticeKind kind)
    {
        StatusNotifications.Show(this._notifications, title, message, kind, key: "file-operation");
    }

    private void RestoreOperationFocus()
    {
        if (this.ViewModel.CanInteract && (this._previousFocus is not { IsLoaded: true } ||
                                           !this._previousFocus.Focus(FocusState.Programmatic)))
        {
            this.FileButton.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateInteraction()
    {
        this.Commands.IsEnabled = this.SourceEditor.IsEnabled = this.TargetView.IsEnabled = this.ViewModel.CanInteract;
        this.SourceEditor.Editor.ReadOnly = !this.ViewModel.CanInteract;
        this.Keys.IsEnabled = this.Values.IsEnabled = this.ViewModel.CanPreview;
    }

    private void LoadEditorSource()
    {
        this.SetEditorText();
        this.SourceEditor.Editor.EmptyUndoBuffer();
        this.SourceEditor.Editor.SetSavePoint();
        this.SourceEditor.Editor.GotoLine(0);
        this.UpdateLineMargin();
        this.UpdateHistory();
    }

    private void SetEditorText()
    {
        if (this.SourceEditor.Editor.GetText(this.SourceEditor.Editor.Length + 1) == this.ViewModel.Text)
        {
            return;
        }

        this._loadingSource = true;
        try
        {
            this.SourceEditor.Editor.ReadOnly = false;
            this.SourceEditor.Editor.SetText(this.ViewModel.Text);
        }
        finally
        {
            this.SourceEditor.Editor.ReadOnly = !this.ViewModel.CanInteract;
            this._loadingSource = false;
        }
    }

    private void SetEditorSavePoint()
    {
        this.SourceEditor.Editor.SetSavePoint();
    }

    private void Source_Modified(Editor sender, ModifiedEventArgs args)
    {
        if (this._loadingSource || this.ViewModel is null || (args.ModificationType & 3) == 0)
        {
            return;
        }

        this.UpdateLineMargin();
        this._synchronizingSource = true;
        try
        {
            this.ViewModel.Text = sender.GetText(sender.Length + 1);
        }
        finally
        {
            this._synchronizingSource = false;
        }
    }

    private void Source_UpdateUI(Editor sender, UpdateUIEventArgs args)
    {
        if (this.ViewModel is null)
        {
            return;
        }

        this.UpdateHistory();
        var line = sender.LineFromPosition(sender.CurrentPos) + 1;
        this.ViewModel.SelectSourceLine((int)line);
        this.CaretStatus.Text = $"Ln {line}, Col {sender.GetColumn(sender.CurrentPos) + 1}";
    }

    private void RenderDocument()
    {
        if (this.ViewModel.Document is not { } document)
        {
            return;
        }

        var expandedPaths = this._nodes.Where(pair => pair.Value.IsExpanded).Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        this._synchronizedNode = this.Keys.SelectedNode;
        this.Keys.RootNodes.Clear();
        this._nodes.Clear();
        this._paths.Clear();
        foreach (var section in document.Sections)
        {
            TreeViewNode? parent = null;
            var path = string.Empty;
            foreach (var part in section.Path.Split('\\'))
            {
                path = path.Length == 0 ? part : path + "\\" + part;
                if (!this._nodes.TryGetValue(path, out var node))
                {
                    node = new TreeViewNode
                    {
                        Content = new RegistryFilePreviewKey(part, document.KeyChanges[path]),
                        IsExpanded = parent is null || expandedPaths.Contains(path)
                    };
                    this._nodes.Add(path, node);
                    this._paths.Add(node, path);
                    if (parent is null)
                    {
                        this.Keys.RootNodes.Add(node);
                    }
                    else
                    {
                        parent.Children.Add(node);
                    }
                }

                parent = node;
            }
        }

        this.EmptyPreview.Visibility = document.Sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        this.SelectNode(this.ViewModel.SelectedKeyPath);
    }

    private void SelectNode(string? path)
    {
        this.SelectedPath.Text = path ?? "No key instructions in this file.";
        ToolTipService.SetToolTip(this.SelectedPath, this.SelectedPath.Text);
        if (path is null || !this._nodes.TryGetValue(path, out var node))
        {
            return;
        }

        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
        }

        // WinUI can deliver SelectionChanged after this assignment has returned.
        this._synchronizedNode = node;
        this.Keys.SelectedNode = node;
    }

    private void Keys_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode is not { } node || ReferenceEquals(node, this._synchronizedNode) ||
            !this._paths.TryGetValue(node, out var path))
        {
            return;
        }

        this._synchronizedNode = node;
        this.ViewModel.SelectedKeyPath = path;
        this.ViewModel.GoToSelectedKeyCommand.Execute(null);
    }

    private void GoToSourceLine(int line, bool focus)
    {
        this.SourceEditor.Editor.EnsureVisible(line - 1);
        this.SourceEditor.Editor.GotoLine(line - 1);
        if (focus)
        {
            this.SourceEditor.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateHistory()
    {
        this.ViewModel.SetEditorHistory(this.SourceEditor.Editor.CanUndo(), this.SourceEditor.Editor.CanRedo());
    }

    private void EditHistory(bool redo)
    {
        if (redo)
        {
            this.SourceEditor.Editor.Redo();
        }
        else
        {
            this.SourceEditor.Editor.Undo();
        }

        this.UpdateHistory();
        this.SourceEditor.Focus(FocusState.Programmatic);
    }

    private void Command_Click(object sender, RoutedEventArgs args)
    {
        AccessKeyManager.ExitDisplayMode();
    }

    private void Preview_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (this.ViewModel?.CanInteract != true ||
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == 0 ||
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0 ||
            args.Key is not (VirtualKey.S or VirtualKey.O or VirtualKey.Z or VirtualKey.Y or VirtualKey.W
                or (VirtualKey)188))
        {
            return;
        }

        args.Handled = true;
        this.InvokeShortcut(
            args.Key,
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0);
    }

    private void Shortcut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        this.InvokeShortcut(sender.Key, (sender.Modifiers & VirtualKeyModifiers.Shift) != 0);
    }

    private void InvokeShortcut(VirtualKey key, bool shift)
    {
        if (this.ViewModel is not { } model)
        {
            return;
        }

        ICommand command = key switch
        {
            (VirtualKey)188 => model.SettingsCommand,
            VirtualKey.W => model.CloseCommand,
            VirtualKey.O => model.OpenCommand,
            VirtualKey.S => shift ? model.SaveAsCommand : model.SaveCommand,
            VirtualKey.Y => model.RedoCommand,
            _ => shift ? model.RedoCommand : model.UndoCommand
        };
        if (command.CanExecute(null))
        {
            AccessKeyManager.ExitDisplayMode();
            command.Execute(null);
        }
    }

    private void Layout_AccessKeyInvoked(UIElement sender, AccessKeyInvokedEventArgs args)
    {
        this.LayoutModes.SelectedItem = sender;
        args.Handled = true;
    }

    private void LayoutItem_GotFocus(object sender, RoutedEventArgs args)
    {
        if (sender is ListViewItem { FocusState: FocusState.Keyboard } item)
        {
            this.LayoutModes.SelectedItem = item;
        }
    }

    private void Layout_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (this.ViewModel is null || this.LayoutModes.SelectedItem is null)
        {
            return;
        }

        this.ViewModel.Layout = (RegistryFileLayout)this.LayoutModes.SelectedIndex;
        this.ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (this.EditorPane.Visibility == Visibility.Visible && this.PreviewPane.Visibility == Visibility.Visible)
        {
            this._splitSourceWidth = this.SourceColumn.Width;
        }

        var editor = this.ViewModel.Layout != RegistryFileLayout.Preview;
        var preview = this.ViewModel.Layout != RegistryFileLayout.Editor;
        var both = editor && preview;
        this.EditorPane.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
        this.PreviewPane.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        this.SourceDivider.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        this.SourceColumn.MinWidth = editor ? 320 : 0;
        this.PreviewColumn.MinWidth = preview ? 320 : 0;
        this.SourceColumn.Width
            = editor ? both ? this._splitSourceWidth : new GridLength(1, GridUnitType.Star) : new GridLength(0);
        this.PreviewColumn.Width = preview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        this.SplitColumn.Width = new GridLength(both ? 8 : 0);
    }

    private void SourceResize_DragDelta(object sender, DragDeltaEventArgs args)
    {
        this.ResizeSource(args.HorizontalChange);
    }

    private void ValuesResize_DragDelta(object sender, DragDeltaEventArgs args)
    {
        this.ResizeValues(args.VerticalChange);
    }

    private void SourceResize_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Left or VirtualKey.Right)
        {
            this.ResizeSource(args.Key == VirtualKey.Left ? -24 : 24);
            args.Handled = true;
        }
    }

    private void ValuesResize_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Up or VirtualKey.Down)
        {
            this.ResizeValues(args.Key == VirtualKey.Up ? -24 : 24);
            args.Handled = true;
        }
    }

    private void ResizeSource(double delta)
    {
        var width = Math.Clamp(
            this.SourceColumn.ActualWidth + delta, 320, Math.Max(320, this.Workspace.ActualWidth - 328));
        this.SourceColumn.Width = new GridLength(width / (this.Workspace.ActualWidth - width - 8), GridUnitType.Star);
        this.PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void ResizeValues(double delta)
    {
        this.KeysRow.Height = new GridLength(
            Math.Clamp(
                this.KeysRow.ActualHeight + delta, 80,
                Math.Max(80, this.KeysRow.ActualHeight + this.ValuesRow.ActualHeight - 80)));
    }

    private void UpdateMetadata(RegistryFileMetadata metadata)
    {
        var wasHidden = this.MetadataPanel.Visibility != Visibility.Visible;
        this.MetadataPanel.Visibility = metadata.HasContent ? Visibility.Visible : Visibility.Collapsed;
        if (metadata.HasContent && wasHidden)
        {
            this.MetadataPanel.IsExpanded = true;
        }

        SetText(this.MetadataTitle, metadata.Title);
        SetText(this.MetadataDescription, metadata.Description);
        SetText(this.MetadataAuthor, metadata.Author.Length == 0 ? "" : "Author: " + metadata.Author);
        SetText(
            this.MetadataFollowUp,
            metadata.FollowUp == RegistryFileFollowUp.None ? "" : "After import: " + metadata.FollowUpText);

        static void SetText(TextBlock block, string text)
        {
            block.Text = text;
            block.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
