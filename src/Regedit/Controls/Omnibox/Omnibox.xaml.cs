// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using System.ComponentModel;
using JPSoftworks.DesktopToolkit.Controls;
using Microsoft.UI.Content;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace JPSoftworks.Regedit.Controls.Omnibox;

public sealed partial class Omnibox : UserControl
{
    public event EventHandler<OmniboxNavigationRequestedEventArgs>? NavigationRequested;

    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(
        nameof(Address), typeof(string), typeof(Omnibox),
        new PropertyMetadata(string.Empty, (sender, _) => ((Omnibox)sender).EndEdit(false)));

    public static readonly DependencyProperty BreadcrumbsProperty = DependencyProperty.Register(
        nameof(Breadcrumbs), typeof(object), typeof(Omnibox),
        new PropertyMetadata(null, (sender, _) => ((Omnibox)sender).RebuildBreadcrumbs()));

    public static readonly DependencyProperty IsTextModeProperty = DependencyProperty.Register(
        nameof(IsTextMode), typeof(bool), typeof(Omnibox),
        new PropertyMetadata(false, (sender, _) => ((Omnibox)sender).RebuildBreadcrumbs()));

    public static readonly DependencyProperty IsRootSelectorVisibleProperty = DependencyProperty.Register(
        nameof(IsRootSelectorVisible), typeof(bool), typeof(Omnibox),
        new PropertyMetadata(true, (sender, _) => ((Omnibox)sender).RebuildBreadcrumbs()));

    public static readonly DependencyProperty LocationIconSourceProperty = DependencyProperty.Register(
        nameof(LocationIconSource), typeof(IconSource), typeof(Omnibox),
        new PropertyMetadata(null, (sender, _) => ((Omnibox)sender).UpdateLocationIcon()));

    private readonly List<(OmniboxItem Item, StackPanel Panel, Button Label, Button Children)> _segments = [];
    private CancellationTokenSource? _suggestionsCancellation;
    private CancellationTokenSource? _childrenCancellation;
    private SearchableItemsFlyout? _childrenMenu;
    private MenuFlyout? _suggestionMenu;
    private VirtualKey? _actionsKeyDown;
    private Func<string?, CancellationToken, Task<IReadOnlyList<OmniboxItem>>>? _childrenProvider;
    private int _firstVisible;
    private bool _isEnteringEditMode;
    private (string Draft, bool Backwards)? _pendingTab;
    private Border? _suggestionsPanel;
    private Action? _detachSuggestionLayout;
    private OmniboxScope? _scope;
    private string _queryText = string.Empty;
    // Opening the editor requests an empty query while the textbox still displays the current address.
    private string _providerQuery = string.Empty;
    private OmniboxItem? _previewItem;
    private readonly double _addressTextInset;
    private ListView? _suggestionsList;
    private IReadOnlyList<OmniboxItem> _suggestions = [];
    private readonly ObservableCollection<OmniboxItem> _suggestionRows = [];
    private OmniboxSuggestionGroup[] _suggestionGroups = [];
    private ICollectionView? _groupedSuggestionsView;
    private readonly HashSet<string> _expandedSuggestionGroups = new(StringComparer.Ordinal);

    public string Address
    {
        get => (string)this.GetValue(AddressProperty);
        set => this.SetValue(AddressProperty, value);
    }

    public IEnumerable<OmniboxItem>? Breadcrumbs
    {
        get => (IEnumerable<OmniboxItem>?)this.GetValue(BreadcrumbsProperty);
        set => this.SetValue(BreadcrumbsProperty, value);
    }

    public bool IsRootSelectorVisible
    {
        get => (bool)this.GetValue(IsRootSelectorVisibleProperty);
        set => this.SetValue(IsRootSelectorVisibleProperty, value);
    }

    public bool IsTextMode
    {
        get => (bool)this.GetValue(IsTextModeProperty);
        set => this.SetValue(IsTextModeProperty, value);
    }

    public IconSource? LocationIconSource
    {
        get => (IconSource?)this.GetValue(LocationIconSourceProperty);
        set => this.SetValue(LocationIconSourceProperty, value);
    }

    public Func<string?, CancellationToken, Task<IReadOnlyList<OmniboxItem>>>? ChildrenProvider
    {
        get => this._childrenProvider;
        set
        {
            this._childrenProvider = value;
            this.RebuildBreadcrumbs();
        }
    }

    public Func<string, CancellationToken, Task<IReadOnlyList<OmniboxItem>>>? SuggestionProvider { get; set; }

    public Func<string, IReadOnlyList<OmniboxAction>>? SuggestionActionProvider { get; set; }

    public bool ShowSuggestionsOnOpen { get; set; }

    public IReadOnlyList<OmniboxScope> Scopes { get; set; } = Array.Empty<OmniboxScope>();

    public string AddressPlaceholder { get; set; } = "Enter an address";

    public UIElement? TrailingContent
    {
        get => this.TrailingPresenter.Content as UIElement;
        set => this.TrailingPresenter.Content = value;
    }

    public Omnibox()
    {
        this.InitializeComponent();
        this.EditorPopup.PlacementTarget = this.AddressHost;
        var iconInset = ((Thickness)this.Resources["SuggestionPopupBorderThickness"]).Left +
                        ((Thickness)this.Resources["SuggestionListMargin"]).Left +
                        ((Thickness)this.Resources["SuggestionRowPadding"]).Left;
        this.AddressEditorIcon.Margin = new Thickness(iconInset, 0, 0, 0);
        this._addressTextInset = iconInset + this.AddressEditorIcon.Width + (double)this.Resources["SuggestionColumnSpacing"];
        this.ClearInputButton.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => this.UpdateInputPadding());
        this.Loaded += (_, _) => this.UpdateOverflow();
        this.RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => this.UpdateOverflow());
        this.Unloaded += (_, _) =>
        {
            this.EndEdit(false);
            this._detachSuggestionLayout?.Invoke();
        };
    }

    private void AttachSuggestionLayout(TextBox input)
    {
        input.ApplyTemplate();
        if (FindVisualChild<Button>(input, "DeleteButton") is { } nativeClearButton)
        {
            // The popup's clear button reserves the same space with and without textbox focus.
            nativeClearButton.MinWidth = 0;
            nativeClearButton.Width = 0;
            nativeClearButton.Opacity = 0;
            nativeClearButton.IsHitTestVisible = false;
        }

        var inputRow = (Grid)VisualTreeHelper.GetParent(input);
        var layout = (Grid)VisualTreeHelper.GetParent(inputRow);
        var chrome = inputRow.Children.OfType<Border>().Single();
        var panel = layout.Children.OfType<Border>().Single();
        if (ReferenceEquals(this._suggestionsPanel, panel))
        {
            return;
        }

        this._detachSuggestionLayout?.Invoke();
        var rounded = this.Editor.CornerRadius;
        var roundedTop = new CornerRadius(rounded.TopLeft, rounded.TopRight, 0, 0);
        var roundedBottom = new CornerRadius(0, 0, rounded.BottomRight, rounded.BottomLeft);
        var suggestionsList = ((Grid)panel.Child).Children.OfType<ListView>().Single();
        this._suggestionsList = suggestionsList;
        this._suggestionsPanel = panel;
        suggestionsList.ItemsSource = this.Editor.ItemsSource;

        void CancelTabOnEdit(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
        {
            if (this._pendingTab is { } tab && args.NewText != tab.Draft)
            {
                this._pendingTab = null;
            }
        }

        void RestorePreviewSelection(object sender, TextChangedEventArgs args)
        {
            // AutoSuggestBox can clear the row selection while applying a queued preview to its textbox.
            if (suggestionsList.SelectedIndex == -1 && this._previewItem is { } preview &&
                input.Text == this.PreviewText && input.Text != this._queryText &&
                this.IsSuggestionListOpen && suggestionsList.Items.Contains(preview))
            {
                suggestionsList.SelectedItem = preview;
            }
        }

        void UpdateEditorFrame()
        {
            var above = this.IsEditorPopupAbove;
            Grid.SetRow(inputRow, above ? 1 : 0);
            Grid.SetRow(panel, above ? 0 : 1);
            this.EditorAccessories.VerticalAlignment = above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            this.UpdateEditorPopupOffset();

            if (panel.Visibility == Visibility.Collapsed)
            {
                suggestionsList.SelectedIndex = -1;
                chrome.CornerRadius = rounded;
                panel.CornerRadius = rounded;
                return;
            }

            chrome.CornerRadius = above ? roundedBottom : roundedTop;
            panel.CornerRadius = above ? roundedTop : roundedBottom;
        }

        // AutoSuggestBox clears its open flag when focus moves into the results.
        void UpdateSuggestionVisibility(DependencyObject sender, DependencyProperty property)
        {
            if (!this.Editor.IsSuggestionListOpen && panel.Visibility == Visibility.Visible &&
                (this._isEnteringEditMode || this._suggestionMenu is not null || (this.XamlRoot is { } root &&
                    Contains(suggestionsList, FocusManager.GetFocusedElement(root) as DependencyObject))))
            {
                return;
            }

            panel.Visibility = this.Editor.IsSuggestionListOpen && this.Editor.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        void PlacementChanged(object? sender, object args) => UpdateEditorFrame();

        void SuppressTextMenuForRowAction(object sender, ContextMenuEventArgs args)
        {
            if (this._actionsKeyDown is not null)
            {
                args.Handled = true;
            }
        }

        input.BeforeTextChanging += CancelTabOnEdit;
        input.TextChanged += RestorePreviewSelection;
        input.ContextMenuOpening += SuppressTextMenuForRowAction;
        var visibilityToken = this.Editor.RegisterPropertyChangedCallback(AutoSuggestBox.IsSuggestionListOpenProperty, UpdateSuggestionVisibility);
        var panelToken = panel.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateEditorFrame());
        this.EditorPopup.ActualPlacementChanged += PlacementChanged;
        this._detachSuggestionLayout = Unsubscribe;
        UpdateSuggestionVisibility(this.Editor, AutoSuggestBox.IsSuggestionListOpenProperty);
        UpdateEditorFrame();

        void Unsubscribe()
        {
            input.BeforeTextChanging -= CancelTabOnEdit;
            input.TextChanged -= RestorePreviewSelection;
            input.ContextMenuOpening -= SuppressTextMenuForRowAction;
            this.EditorPopup.ActualPlacementChanged -= PlacementChanged;
            this.Editor.UnregisterPropertyChangedCallback(AutoSuggestBox.IsSuggestionListOpenProperty, visibilityToken);
            panel.UnregisterPropertyChangedCallback(VisibilityProperty, panelToken);
            this._suggestionsPanel = null;
            this._suggestionsList = null;
            this._detachSuggestionLayout = null;
        }
    }

    public void SetLocation(string address, IEnumerable<OmniboxItem> breadcrumbs)
    {
        var focused = this.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        var restoreFocus = Contains(this, focused) || Contains(this.EditorSurface, focused);
        this.Breadcrumbs = breadcrumbs;
        this.Address = address;
        this.EndEdit(restoreFocus);
    }

    public void BeginEdit()
    {
        this.BeginEdit(null);
    }

    public void BeginEdit(OmniboxScope? scope)
    {
        this._isEnteringEditMode = true;
        void FocusOnOpen() => this.FocusEditor(showInitialSuggestions: true);

        var contextMenu = ((FrameworkElement)this.Content).ContextFlyout;
        var menu = this._childrenMenu ?? (this.OverflowMenu.IsOpen ? this.OverflowMenu : contextMenu);
        if (menu is { IsOpen: true })
        {
            // Flyouts restore focus when they close, so focus the editor afterward.
            void OnClosed(object? sender, object args)
            {
                menu.Closed -= OnClosed;
                this.DispatcherQueue.TryEnqueue(FocusOnOpen);
            }

            menu.Closed += OnClosed;
        }
        else
        {
            this.DispatcherQueue.TryEnqueue(FocusOnOpen);
        }

        this.CloseChildrenMenu();
        this.OverflowMenu.Hide();
        contextMenu?.Hide();
        this.CancelSuggestions();
        this.SetScope(scope);
        this._previewItem = null;
        this._queryText = scope is null ? this.Address : string.Empty;
        this.Editor.Text = this._queryText;
        this.OpenEditorPopup();
    }

    private void OpenEditorPopup()
    {
        if (!this.EditorPopup.IsOpen)
        {
            this.EditPlaceholder.MinWidth = this.DisplayPath.DesiredSize.Width;
            this.EditPlaceholder.MinHeight = this.ActualHeight;
            this.EditPlaceholder.Visibility = Visibility.Visible;
        }

        this.UpdateEditorPopupSize();
        this.DisplayPath.Visibility = Visibility.Collapsed;
        this.Editor.Visibility = Visibility.Visible;
        this.EditorPopup.IsOpen = true;
    }

    private bool IsEditorPopupAbove => this.EditorPopup.ActualPlacement is
        PopupPlacementMode.Top or PopupPlacementMode.TopEdgeAlignedLeft or PopupPlacementMode.TopEdgeAlignedRight;

    private void AddressHost_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (this.EditorPopup.IsOpen)
        {
            this.UpdateEditorPopupSize();
        }
    }

    private void UpdateEditorPopupSize()
    {
        this.EditorSurface.Width = this.ActualWidth;
        this.Editor.MinHeight = Math.Max(this.ActualHeight, this.AddressHost.MinHeight);
        this.EditorAccessories.Height = this.Editor.MinHeight;
        if (this.XamlRoot is { } root)
        {
            const double heightLimit = 560;
            var bounds = this.AddressHost.TransformToVisual(null)
                .TransformBounds(new Rect(0, 0, this.ActualWidth, this.Editor.MinHeight));
            var converter = ContentCoordinateConverter.CreateForWindowId(root.ContentIslandEnvironment.AppWindowId);
            var anchor = converter.ConvertLocalToScreen(bounds);
            var display = DisplayArea.GetFromRect(anchor, DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            // WorkArea is relative to the display, while the anchor is in screen coordinates.
            var workTop = display.OuterBounds.Y + work.Y;
            var above = (anchor.Y - workTop) / root.RasterizationScale;
            var below = (workTop + work.Height - anchor.Y - anchor.Height) / root.RasterizationScale;
            var openAbove = below < heightLimit && above > below;
            this.EditorPopup.DesiredPlacement = openAbove
                ? PopupPlacementMode.TopEdgeAlignedLeft : PopupPlacementMode.BottomEdgeAlignedLeft;
            this.Editor.MaxSuggestionListHeight = Math.Clamp(openAbove ? above : below, 0, heightLimit);
        }

        this.UpdateEditorPopupOffset();
    }

    private void UpdateEditorPopupOffset()
    {
        // The edit surface overlaps the address row and extends toward the available space.
        this.EditorPopup.VerticalOffset = this.IsEditorPopupAbove ? this.Editor.MinHeight : -this.Editor.MinHeight;
    }

    private void FocusEditor(bool showInitialSuggestions)
    {
        if (this.Editor.Visibility != Visibility.Visible)
        {
            return;
        }

        // The newly visible editor must finish layout before its inner textbox can receive focus.
        this.Editor.UpdateLayout();
        if (FindVisualChild<TextBox>(this.Editor) is not { } textBox)
        {
            return;
        }

        this.AttachSuggestionLayout(textBox);
        if (textBox.Focus(FocusState.Keyboard))
        {
            this.UpdateInputPadding();
            if (this._scope is { } scope)
            {
                textBox.Select(textBox.Text.Length, 0);
                this.ShowScopedSuggestions(scope, this.Editor.Text);
            }
            else
            {
                textBox.SelectAll();
                if (showInitialSuggestions && this.ShowSuggestionsOnOpen)
                {
                    _ = this.RequestSuggestionsAsync(string.Empty);
                }
            }

            this._isEnteringEditMode = false;
        }
    }

    private void EndEdit(bool restoreFocus)
    {
        this._isEnteringEditMode = false;
        this.CloseChildrenMenu();
        this.OverflowMenu.Hide();
        this.CancelSuggestions();
        this.SetScope(null);
        this.Editor.Text = this.Address;
        this.CloseEditorPopup();
        if (restoreFocus)
        {
            this.EditAddressButton.Focus(FocusState.Programmatic);
        }
    }

    private void CloseEditorPopup()
    {
        this.Editor.Visibility = Visibility.Collapsed;
        this.DisplayPath.Visibility = Visibility.Visible;
        this.EditPlaceholder.Visibility = Visibility.Collapsed;
        this.EditorPopup.IsOpen = false;
    }

    private void SetScope(OmniboxScope? scope)
    {
        this._scope = scope;
        this.ScopeToken.Visibility = scope is null ? Visibility.Collapsed : Visibility.Visible;
        this.ScopeLabel.Text = scope?.Label ?? string.Empty;
        var tokenLabel = scope is null ? string.Empty : $"Clear {scope.Label} context";
        AutomationProperties.SetName(this.ScopeToken, tokenLabel);
        ToolTipService.SetToolTip(this.ScopeToken, tokenLabel);
        this.Editor.PlaceholderText = scope?.Placeholder ?? this.AddressPlaceholder;
        AutomationProperties.SetName(this.Editor, scope?.Placeholder ?? "Address");
        this.Editor.QueryIcon = scope is null ? new SymbolIcon(Symbol.Forward) : null;
        this.ClearInputButton.Margin = new Thickness(0, 0, scope is null ? 36 : 2, 0);
        this.UpdateInputPadding();
    }

    private void ChangeScope(OmniboxScope? scope, string query)
    {
        this._isEnteringEditMode = true;
        this._pendingTab = null;
        if (scope is null)
        {
            this.CancelSuggestions();
        }
        else
        {
            // Keep the current rows visible until the scoped results are ready.
            this._suggestionsCancellation?.Cancel();
        }

        this.SetScope(scope);
        this._previewItem = null;
        this._queryText = query;
        this.Editor.Text = query;
        this.DispatcherQueue.TryEnqueue(() => this.FocusEditor(showInitialSuggestions: query.Length == 0));
    }

    private void UpdateInputPadding()
    {
        if (FindVisualChild<TextBox>(this.Editor) is { } input)
        {
            input.ClearValue(PaddingProperty);
            var padding = input.Padding;
            var left = this._scope is null
                ? Math.Max(0, this._addressTextInset - input.BorderThickness.Left)
                : this.ScopeToken.ActualWidth + 12;
            var right = padding.Right + (this.ClearInputButton.Visibility == Visibility.Visible ? this.ClearInputButton.Width : 0);
            input.Padding = new Thickness(left, padding.Top, right, padding.Bottom);

            AutomationProperties.SetAutomationId(input, "OmniboxInput");
            AutomationProperties.SetName(input, this._scope?.Placeholder ?? "Address");
        }
    }

    private void ScopeToken_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.UpdateInputPadding();
    }

    private void ScopeToken_Click(object sender, RoutedEventArgs args)
    {
        this.ChangeScope(null, this.Editor.Text);
    }

    private void ClearInputButton_Click(object sender, RoutedEventArgs args)
    {
        if (FindVisualChild<TextBox>(this.Editor) is { } input)
        {
            input.Focus(FocusState.Keyboard);
            input.Text = string.Empty;
        }
    }

    private OmniboxScope? FindScope(string keyword)
    {
        return this.Scopes.FirstOrDefault(scope => keyword.Equals(scope.Keyword, StringComparison.OrdinalIgnoreCase) ||
                                                   keyword.Equals(scope.Alias, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesScopePrefix(OmniboxScope scope, string text)
    {
        return text.Length != 0 && (scope.Keyword.StartsWith(text, StringComparison.OrdinalIgnoreCase) ||
                                    text.Equals(scope.Alias, StringComparison.OrdinalIgnoreCase));
    }

    private OmniboxScope? FindTabScope(string text)
    {
        if (this.FindScope(text) is { } exact)
        {
            return exact;
        }

        return this.Scopes.Where(scope => MatchesScopePrefix(scope, text)).Take(2).ToArray() is [var match] ? match : null;
    }

    private void SetSuggestions(IReadOnlyList<OmniboxItem> suggestions, string? footer = null)
    {
        this._suggestions = suggestions;
        this._expandedSuggestionGroups.Clear();
        this.Editor.Description = footer ?? this._scope?.Footer ?? (suggestions is [OmniboxItem { Scope: not null }]
            ? "Tab Complete   ·   Esc Close"
            : "↑↓ / Tab Select   ·   Enter Choose   ·   Esc Close");
        this.UpdateSuggestionItems();
        this.SetSuggestionRowsSelectable(true);
        this.SetSuggestionListOpen(suggestions.Count > 0);
        if (this._pendingTab is { } tab && tab.Draft == this._queryText)
        {
            this._pendingTab = null;
            if (this.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox input &&
                input.Text == tab.Draft && Contains(this.Editor, input))
            {
                this.NavigateByTab(tab.Backwards);
            }
        }
    }

    private void SetSuggestionRowsSelectable(bool selectable)
    {
        if (this._suggestionsList is { } list)
        {
            list.SelectionMode = selectable ? ListViewSelectionMode.Single : ListViewSelectionMode.None;
            list.IsItemClickEnabled = selectable;
        }
    }

    private void UpdateSuggestionItems()
    {
        if (this._suggestions.Any(item => !string.IsNullOrEmpty(item.Group)))
        {
            var groups = this._suggestions.GroupBy(item => item.Group ?? string.Empty)
                .Select(this.UpdateSuggestionGroup).ToArray();
            if (!this._suggestionGroups.SequenceEqual(groups))
            {
                // WinUI can reject removing a populated group from a live grouped view.
                this._suggestionGroups = groups;
                this._groupedSuggestionsView = null;
            }

            var view = this._groupedSuggestionsView ??= new CollectionViewSource
            {
                IsSourceGrouped = true,
                Source = this._suggestionGroups
            }.View;

            if (!ReferenceEquals(this.Editor.ItemsSource, view))
            {
                this.Editor.ItemsSource = view;
            }

            if (this._suggestionsList is { } groupedList && !ReferenceEquals(groupedList.ItemsSource, view))
            {
                groupedList.ItemsSource = view;
            }

            return;
        }

        UpdateRows(this._suggestionRows, this._suggestions);
        if (!ReferenceEquals(this.Editor.ItemsSource, this._suggestionRows))
        {
            // C#/WinRT needs the concrete collection type to generate its projection.
            this.Editor.ItemsSource = this._suggestionRows;
        }

        if (this._suggestionsList is { } list && !ReferenceEquals(list.ItemsSource, this._suggestionRows))
        {
            list.ItemsSource = this._suggestionRows;
        }
    }

    private OmniboxSuggestionGroup UpdateSuggestionGroup(IGrouping<string, OmniboxItem> group)
    {
        var existing = this._suggestionGroups.FirstOrDefault(candidate => candidate.Key == group.Key) ??
                       new OmniboxSuggestionGroup(group.Key);
        var items = group.ToArray();
        if (group.Key.Length > 0 && this._scope?.GroupPreviewCounts?.TryGetValue(group.Key, out var count) == true &&
            count >= 0 && count < items.Length && !this._expandedSuggestionGroups.Contains(group.Key))
        {
            var header = new OmniboxItem(group.Key, string.Empty)
            {
                Group = group.Key,
                FirstHiddenResult = items[count]
            };
            existing.Label = string.Empty;
            UpdateRows(existing, (OmniboxItem[])[header, ..items.Take(count)]);
        }
        else
        {
            existing.Label = group.Key;
            UpdateRows(existing, items);
        }

        return existing;
    }

    private static void UpdateRows<T>(ObservableCollection<T> current, IReadOnlyList<T> next)
        where T : notnull
    {
        if (current.SequenceEqual(next))
        {
            return;
        }

        var remaining = next.GroupBy(item => item).ToDictionary(group => group.Key, group => group.Count());
        // Keep a nonempty popup open even when every result changes.
        if (next.Count > 0 && current.Count > 0 && !current.Any(remaining.ContainsKey))
        {
            current[0] = next[0];
        }

        for (var index = current.Count - 1; index >= 0; index--)
        {
            var item = current[index];
            if (remaining.TryGetValue(item, out var count) && count > 0)
            {
                remaining[item] = count - 1;
            }
            else
            {
                current.RemoveAt(index);
            }
        }

        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < next.Count; index++)
        {
            var existing = index;
            while (existing < current.Count && !comparer.Equals(current[existing], next[index]))
            {
                existing++;
            }

            if (existing == current.Count)
            {
                current.Insert(index, next[index]);
            }
            else if (existing != index)
            {
                current.Move(existing, index);
            }
        }
    }

    private void ExpandSuggestionGroup(OmniboxItem header)
    {
        if (header is not { Group: { } group, FirstHiddenResult: { } firstHidden } ||
            !this.Editor.Items.Any(item => ReferenceEquals(item, header)))
        {
            return;
        }

        this._expandedSuggestionGroups.Add(group);
        this.UpdateSuggestionItems();
        if (!this.IsSuggestionListOpen)
        {
            this.SetSuggestionListOpen(true);
        }

        var suggestions = this._suggestions;
        this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(this._suggestions, suggestions) && this._expandedSuggestionGroups.Contains(group) &&
                this.IsSuggestionListOpen &&
                this._suggestionsList is { SelectedIndex: -1 } list)
            {
                list.SelectedIndex = list.Items.IndexOf(firstHidden);
            }
        });
    }

    private void ShowMore_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: OmniboxItem header } button)
        {
            if (button.FocusState != FocusState.Unfocused)
            {
                FindVisualChild<TextBox>(this.Editor)?.Focus(FocusState.Keyboard);
            }

            this.ExpandSuggestionGroup(header);
        }
    }

    private void SuggestionActions_Loaded(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        if (this._suggestionsList?.ContainerFromItem(button.DataContext) is not ListViewItem row)
        {
            return;
        }

        var pointerOver = false;
        void Reveal(bool hovered)
        {
            pointerOver = hovered;
            button.Visibility = hovered || row.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        }

        void Entered(object sender, PointerRoutedEventArgs args) => Reveal(true);
        void Exited(object sender, PointerRoutedEventArgs args) => Reveal(false);
        void ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            if (row.Content is OmniboxItem item && args.TryGetPosition(row, out _))
            {
                this.ShowSuggestionActions(row, item);
                args.Handled = true;
            }
        }

        var selectionToken = row.RegisterPropertyChangedCallback(SelectorItem.IsSelectedProperty, (_, _) => Reveal(pointerOver));
        row.PointerEntered += Entered;
        row.PointerExited += Exited;
        row.ContextRequested += ContextRequested;
        button.Unloaded += Detach;
        Reveal(false);

        void Detach(object sender, RoutedEventArgs args)
        {
            button.Unloaded -= Detach;
            row.PointerEntered -= Entered;
            row.PointerExited -= Exited;
            row.ContextRequested -= ContextRequested;
            row.UnregisterPropertyChangedCallback(SelectorItem.IsSelectedProperty, selectionToken);
        }
    }

    private void SuggestionActions_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: OmniboxItem item } && this._suggestionsList?.ContainerFromItem(item) is ListViewItem row)
        {
            this.ShowSuggestionActions(row, item);
        }
    }

    private void SuggestionActions_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (this.SuggestionActionProvider is not null && this._suggestionsList?.SelectedItem is OmniboxItem { HasActions: true } &&
            this.HasCurrentSuggestionRows(this.InputText))
        {
            // WinUI requests the textbox menu on key-down. Open the row menu once that gesture has finished.
            this._actionsKeyDown = sender.Key;
            args.Handled = true;
        }
    }

    private void Editor_PreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (this._actionsKeyDown != args.Key)
        {
            return;
        }

        args.Handled = true;
        this._actionsKeyDown = null;
        if (this._suggestionsList is { SelectedItem: OmniboxItem item } list && list.ContainerFromItem(item) is FrameworkElement row)
        {
            this.ShowSuggestionActions(row, item);
        }
    }

    private void ShowSuggestionActions(FrameworkElement anchor, OmniboxItem item)
    {
        if (this._suggestionMenu is not null || !this.HasCurrentSuggestionRows(this.InputText) ||
            item.ActionsKey is not { } key || this.SuggestionActionProvider?.Invoke(key) is not { Count: > 0 } actions)
        {
            return;
        }

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        OmniboxAction? chosen = null;
        foreach (var action in actions)
        {
            var entry = new MenuFlyoutItem { Text = action.Label };
            entry.Click += (_, _) => chosen = action;
            menu.Items.Add(entry);
        }

        this._suggestionMenu = menu;
        menu.Closed += async (_, _) =>
        {
            if (!ReferenceEquals(this._suggestionMenu, menu))
            {
                return;
            }

            if (chosen?.CloseEditor == true)
            {
                this.EndEdit(false);
            }
            else if (chosen is not null)
            {
                // The windowed editor popup would cover a dialog shown by the action.
                this.SetSuggestionListOpen(false);
                this.CloseEditorPopup();
            }

            try
            {
                if (chosen is not null)
                {
                    await chosen.ExecuteAsync();
                }
            }
            finally
            {
                if (ReferenceEquals(this._suggestionMenu, menu))
                {
                    if (chosen is not null)
                    {
                        this.OpenEditorPopup();
                        this.Editor.UpdateLayout();
                    }

                    this._suggestionMenu = null;
                    FindVisualChild<TextBox>(this.Editor)?.Focus(FocusState.Programmatic);
                    if (chosen is null)
                    {
                        this.SetSuggestionListOpen(true);
                    }
                    else
                    {
                        await this.RefreshSuggestionsAfterActionAsync();
                    }
                }
            }
        };
        menu.ShowAt(anchor);
    }

    private async Task RefreshSuggestionsAfterActionAsync()
    {
        if (this._previewItem is not null && this._suggestionsList is { } list)
        {
            this.RestoreTypedQuery(list);
        }

        if (this._scope is { } scope)
        {
            this.ShowScopedSuggestions(scope, this._queryText);
        }
        else
        {
            await this.RequestSuggestionsAsync(this._providerQuery);
        }
    }

    private void ShowScopedSuggestions(OmniboxScope scope, string text)
    {
        var suggestions = scope.Suggest(text);
        if (suggestions.Count == 0)
        {
            this.SetSuggestions((OmniboxItem[])[new(scope.EmptyMessage, string.Empty)], "Esc Close");
        }
        else
        {
            this.SetSuggestions(suggestions);
        }
    }

    private void SuggestionsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.IsEnabled = !IsStatusSuggestion(args.Item);
        args.ItemContainer.IsTabStop = args.Item is not OmniboxItem { FirstHiddenResult: not null };
    }

    private static bool IsStatusSuggestion(object item)
    {
        return item is OmniboxItem { Scope: null, Command: null, FirstHiddenResult: null, Address.Length: 0 };
    }

    private void CancelSuggestions()
    {
        this._actionsKeyDown = null;
        var menu = this._suggestionMenu;
        this._suggestionMenu = null;
        menu?.Hide();
        this._suggestionsCancellation?.Cancel();
        this._suggestionsCancellation = null;
        this._pendingTab = null;
        this._suggestions = [];
        this._expandedSuggestionGroups.Clear();
        this.Editor.ItemsSource = null;
        if (this._suggestionsList is { } list)
        {
            list.ItemsSource = null;
        }

        this.SetSuggestionListOpen(false);
        this._suggestionRows.Clear();
        this._suggestionGroups = [];
        this._groupedSuggestionsView = null;
    }

    private string? PreviewText => this._previewItem?.Scope?.Keyword ?? this._previewItem?.Address;

    private string InputText => FindVisualChild<TextBox>(this.Editor)?.Text ?? this.Editor.Text;

    private bool HasCurrentSuggestionRows(string text) => this._suggestionsList is { SelectionMode: ListViewSelectionMode.Single } &&
        (text == this._queryText || text == this.PreviewText);

    private async void Editor_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (this.Editor.Visibility != Visibility.Visible)
        {
            return;
        }

        var text = sender.Text;
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || text == this.PreviewText || text != this.InputText)
        {
            return;
        }

        this._previewItem = null;
        this._queryText = text;
        if (this._scope is { } scope)
        {
            this.ShowScopedSuggestions(scope, text);
            return;
        }

        var shortcut = this.Scopes.FirstOrDefault(candidate =>
            candidate.Alias is { Length: > 0 } alias &&
            text.StartsWith(alias, StringComparison.OrdinalIgnoreCase));
        if (shortcut?.Alias is { } prefix)
        {
            this.ChangeScope(shortcut, text[prefix.Length..]);
            return;
        }

        var separator = text.AsSpan().IndexOfAny(' ', '\t');
        if (separator >= 0 && this.FindScope(text[..separator]) is { } confirmed)
        {
            this.ChangeScope(confirmed, text[(separator + 1)..]);
            return;
        }

        var matchingScopes = this.Scopes.Where(scope => MatchesScopePrefix(scope, text))
            .Select(scope => new OmniboxItem(scope.Keyword, string.Empty)
            {
                Scope = scope,
                Description = scope.Placeholder,
                IconGlyph = scope.IconGlyph,
                RightText = scope.Alias
            }).ToArray();
        if (matchingScopes.Length > 0)
        {
            this._suggestionsCancellation?.Cancel();
            this.SetSuggestions(matchingScopes);
            return;
        }

        await this.RequestSuggestionsAsync(text);
    }

    private async Task RequestSuggestionsAsync(string query)
    {
        if (this.SuggestionProvider is not { } provider)
        {
            this.CancelSuggestions();
            return;
        }

        var initial = this.ShowSuggestionsOnOpen && query.Length == 0;
        var editorText = this.Editor.Text;
        this._suggestionsCancellation?.Cancel();
        this.SetSuggestionRowsSelectable(false);
        var cancellation = new CancellationTokenSource();
        this._suggestionsCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            if (!initial)
            {
                await Task.Delay(120, token);
            }

            var suggestions = await provider(query, token);
            token.ThrowIfCancellationRequested();
            if (this.Editor.Visibility != Visibility.Visible || this.InputText != editorText)
            {
                return;
            }

            this._providerQuery = query;
            this.SetSuggestions(suggestions);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
            {
                this.CancelSuggestions();
            }
        }
        finally
        {
            if (ReferenceEquals(this._suggestionsCancellation, cancellation))
            {
                this._suggestionsCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void Editor_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (this._scope is null && args.SelectedItem is OmniboxItem item && this.HasCurrentSuggestionRows(this.InputText))
        {
            this._previewItem = item;
            sender.Text = item.Scope?.Keyword ?? item.Address;
        }
    }

    private void Editor_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        this.SubmitQuery(args.QueryText, args.ChosenSuggestion as OmniboxItem);
    }

    private void SubmitQuery(string query, OmniboxItem? suggestion)
    {
        var text = this.InputText;
        if (this._scope is null && !this.HasCurrentSuggestionRows(text))
        {
            suggestion = null;
            query = text;
        }

        if (suggestion is { FirstHiddenResult: not null })
        {
            // AutoSuggestBox dismisses its popup after QuerySubmitted returns.
            this.DispatcherQueue.TryEnqueue(() => this.ExpandSuggestionGroup(suggestion));
            return;
        }

        if (this._scope is not null)
        {
            if (suggestion is null && !string.IsNullOrWhiteSpace(query))
            {
                suggestion = this.Editor.Items.OfType<OmniboxItem>().FirstOrDefault(item => item.FirstHiddenResult is null);
            }

            if (suggestion?.Command is { } command && command.CanExecute(suggestion.CommandParameter))
            {
                this.EndEdit(true);
                command.Execute(suggestion.CommandParameter);
            }
            else if (suggestion is { Command: null, Address.Length: > 0 })
            {
                this.CancelSuggestions();
                this.NavigationRequested?.Invoke(this, new OmniboxNavigationRequestedEventArgs(suggestion.Address));
            }

            return;
        }

        if ((suggestion?.Scope ?? this.FindScope(query)) is { } scope)
        {
            this.ChangeScope(scope, string.Empty);
            return;
        }

        var address = suggestion?.Address ?? query;
        this.CancelSuggestions();
        if (!string.IsNullOrWhiteSpace(address) && !this.Scopes.Any(scope => MatchesScopePrefix(scope, address)))
        {
            this.NavigationRequested?.Invoke(this, new OmniboxNavigationRequestedEventArgs(address));
        }
    }

    private void Editor_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            this.EndEdit(true);
            args.Handled = true;
            return;
        }

        var list = this._suggestionsList;
        var source = args.OriginalSource as DependencyObject;
        var fromSuggestions = list is not null && Contains(list, source);
        if (args.Key is VirtualKey.Up or VirtualKey.Down && this.IsSuggestionListOpen &&
            !IsKeyDown(VirtualKey.Control) && !IsKeyDown(VirtualKey.Menu))
        {
            if (!this.HasCurrentSuggestionRows(this.InputText))
            {
                args.Handled = true;
            }
            else if (!IsKeyDown(VirtualKey.Shift) && ReferenceEquals(sender, this.Editor) && list is not null)
            {
                var focusedItem = fromSuggestions ? (source as FrameworkElement)?.DataContext as OmniboxItem : null;
                args.Handled = this.MoveSuggestionSelection(list, args.Key == VirtualKey.Up, wrap: true, focusedItem: focusedItem);
            }
        }
        else if (args.Key == VirtualKey.Enter && fromSuggestions && source is ListViewItem { Content: OmniboxItem item })
        {
            this.SubmitQuery(this.Editor.Text, item);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Enter && !fromSuggestions && this.IsSuggestionListOpen &&
            list?.SelectedItem is OmniboxItem { FirstHiddenResult: not null } header)
        {
            this.ExpandSuggestionGroup(header);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Back && !fromSuggestions && this._scope is not null && this.Editor.Text.Length == 0)
        {
            this.ChangeScope(null, string.Empty);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Z && ReferenceEquals(sender, this.Editor) && !fromSuggestions &&
                 IsKeyDown(VirtualKey.Control) && !IsKeyDown(VirtualKey.Shift) && !IsKeyDown(VirtualKey.Menu) &&
                 list is not null && this.IsSuggestionListOpen && this.PreviewText is { } preview && this.Editor.Text == preview)
        {
            this.RestoreTypedQuery(list);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Tab && !fromSuggestions && ReferenceEquals(sender, this.Editor) &&
                 !IsKeyDown(VirtualKey.Control) && !IsKeyDown(VirtualKey.Menu))
        {
            var backwards = IsKeyDown(VirtualKey.Shift);
            // A fast Tab can reach the textbox before AutoSuggestBox reports the latest text change.
            var text = this.InputText;
            var query = text == this.PreviewText ? this._queryText : text;
            if (!backwards && this._scope is null && this.FindTabScope(query) is { } scope)
            {
                this.ChangeScope(scope, string.Empty);
                args.Handled = true;
            }
            else if (this._scope is null && this.SuggestionProvider is not null &&
                     (query != this._queryText || this._suggestionsCancellation is { IsCancellationRequested: false }))
            {
                this._pendingTab = (query, backwards);
                args.Handled = true;
            }
            else
            {
                args.Handled = this.NavigateByTab(backwards);
            }
        }
    }

    private bool NavigateByTab(bool backwards)
    {
        if (this._suggestionsList is { } list && this.IsSuggestionListOpen)
        {
            return this.MoveSuggestionSelection(list, backwards);
        }

        if (this.XamlRoot?.Content is not { } root)
        {
            return false;
        }

        // Resume tab navigation from the Edit address button.
        this.EndEdit(true);
        var direction = backwards ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
        return FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = root });
    }

    private bool MoveSuggestionSelection(ListView list, bool backwards, bool wrap = false, OmniboxItem? focusedItem = null)
    {
        var step = backwards ? -1 : 1;
        var count = list.Items.Count;
        var edge = backwards ? count - 1 : 0;
        var current = focusedItem is null ? list.SelectedIndex : list.Items.IndexOf(focusedItem);
        var index = current < 0 ? edge : current + step;
        for (var visited = 0; visited < count; visited++, index += step)
        {
            if (index < 0 || index >= count)
            {
                if (!wrap)
                {
                    break;
                }

                index = edge;
            }

            var item = list.Items[index];
            if (IsStatusSuggestion(item))
            {
                continue;
            }

            list.SelectedIndex = index;
            list.ScrollIntoView(item);
            if (focusedItem is not null)
            {
                list.UpdateLayout();
                (list.ContainerFromItem(item) as Control)?.Focus(FocusState.Keyboard);
            }

            return true;
        }

        if (current < 0)
        {
            return wrap;
        }

        if (this._scope is null)
        {
            this.RestoreTypedQuery(list);
        }
        else
        {
            list.SelectedIndex = -1;
        }

        return true;
    }

    private void RestoreTypedQuery(ListView list)
    {
        this._previewItem = null;
        this.Editor.Text = this._queryText;
        list.SelectedIndex = -1;
        if (FindVisualChild<TextBox>(this.Editor) is { } input)
        {
            input.Select(input.Text.Length, 0);
        }
    }

    private bool IsSuggestionListOpen => this._suggestionsPanel?.Visibility == Visibility.Visible;

    private void SetSuggestionListOpen(bool isOpen)
    {
        if (this.Editor.IsSuggestionListOpen != isOpen)
        {
            this.Editor.SetValue(AutoSuggestBox.IsSuggestionListOpenProperty, isOpen);
        }

        if (this._suggestionsPanel is { } panel)
        {
            panel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    }

    private void Editor_LostFocus(object sender, RoutedEventArgs args)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            if (this._isEnteringEditMode || this._suggestionMenu is not null ||
                this.Editor.Visibility != Visibility.Visible || this.XamlRoot is not { } root)
            {
                return;
            }

            var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
            if (this._suggestionsList is { } list && Contains(list, focused))
            {
                return;
            }

            if (!Contains(this.EditorSurface, focused))
            {
                this.EndEdit(false);
            }
            else if (this.IsSuggestionListOpen)
            {
                this.SetSuggestionListOpen(true);
            }
        });
    }

    private void RebuildBreadcrumbs()
    {
        this.CloseChildrenMenu();
        this.OverflowMenu.Hide();
        this.Trail.Children.Clear();
        this._segments.Clear();
        this.TrailViewport.Visibility = this.IsTextMode ? Visibility.Collapsed : Visibility.Visible;
        this.AddressText.Visibility = this.IsTextMode ? Visibility.Visible : Visibility.Collapsed;
        this.RootButton.Visibility = this.IsTextMode || this.ChildrenProvider is null || !this.IsRootSelectorVisible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (this.IsTextMode)
        {
            this.UpdateOverflow();
            return;
        }

        foreach (var item in this.Breadcrumbs ?? [])
        {
            var label = new Button
            {
                Content = new TextBlock { Text = item.Label, TextTrimming = TextTrimming.CharacterEllipsis },
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
                Height = 32,
                MinWidth = 0,
                Padding = new Thickness(6, 0, 6, 0)
            };
            AutomationProperties.SetName(label, item.Label);
            AutomationProperties.SetAutomationId(label, $"Breadcrumb{this._segments.Count}");
            ToolTipService.SetToolTip(label, item.Address);
            label.Click += (_, _) => this.RequestNavigation(item);
            var children = new Button
            {
                Content = new FontIcon { Glyph = "\uE76C", FontSize = 10 },
                Style = label.Style,
                Width = 28,
                Height = 32,
                Padding = new Thickness(0),
                Tag = item.Address,
                Visibility = this.ChildrenProvider is null || item.HasChildren == false ? Visibility.Collapsed : Visibility.Visible
            };
            AutomationProperties.SetName(children, $"Children of {item.Label}");
            AutomationProperties.SetAutomationId(children, $"BreadcrumbChildren{this._segments.Count}");
            ToolTipService.SetToolTip(children, $"Children of {item.Label}");
            children.Click += this.BreadcrumbMenuButton_Click;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(label);
            panel.Children.Add(children);
            if (this.ChildrenProvider is null && this._segments.Count > 0)
            {
                panel.Children.Insert(0, new FontIcon { Glyph = "\uE76C", FontSize = 10, Margin = new Thickness(2, 0, 2, 0) });
            }

            this.Trail.Children.Add(panel);
            this._segments.Add((item, panel, label, children));
        }

        if (this._segments.Count > 0)
        {
            AutomationProperties.SetItemStatus(this._segments[^1].Label, "Current location");
        }

        this.UpdateOverflow();
    }

    private void TrailViewport_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.UpdateOverflow();
    }

    private void UpdateOverflow()
    {
        if (this._segments.Count == 0)
        {
            this._firstVisible = 0;
            this.OverflowButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (this.TrailViewport.ActualWidth <= 0)
        {
            return;
        }

        var widths = new double[this._segments.Count];
        for (var i = 0; i < this._segments.Count; i++)
        {
            var segment = this._segments[i];
            segment.Panel.Visibility = Visibility.Visible;
            segment.Label.MaxWidth = double.PositiveInfinity;
            segment.Panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths[i] = segment.Panel.DesiredSize.Width;
        }

        this._firstVisible = OmniboxLayout.FirstVisible(widths, this.TrailViewport.ActualWidth, this.OverflowButton.Width);
        this.OverflowButton.Visibility = this._firstVisible > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < this._firstVisible; i++)
        {
            this._segments[i].Panel.Visibility = Visibility.Collapsed;
        }

        var last = this._segments[^1];
        var preceding = widths.Skip(this._firstVisible).Take(widths.Length - this._firstVisible - 1).Sum();
        var available = this.TrailViewport.ActualWidth - (this._firstVisible > 0 ? this.OverflowButton.Width : 0) - preceding;
        last.Label.MaxWidth = Math.Max(0, available - (widths[^1] - last.Label.DesiredSize.Width));
    }

    private void OverflowMenu_Opening(object sender, object args)
    {
        this.CloseChildrenMenu();
        this.OverflowMenu.Items.Clear();
        foreach (var segment in this._segments.Take(this._firstVisible))
        {
            this.OverflowMenu.Items.Add(this.NavigationItem(segment.Item));
        }
    }

    private void RequestNavigation(OmniboxItem item)
    {
        if (item.Address != this.Address)
        {
            this.NavigationRequested?.Invoke(this, new OmniboxNavigationRequestedEventArgs(item.Address));
        }
    }

    private MenuFlyoutItem NavigationItem(OmniboxItem item)
    {
        var menuItem = new MenuFlyoutItem { Text = item.Label };
        ToolTipService.SetToolTip(menuItem, item.Address);
        menuItem.Click += (_, _) => this.RequestNavigation(item);
        return menuItem;
    }

    private void BreadcrumbMenuButton_Click(object sender, RoutedEventArgs args)
    {
        // WinUI opens Button.Flyout after the Click handlers return.
        this.GetBreadcrumbMenu((Button)sender);
    }

    private FlyoutBase GetBreadcrumbMenu(Button button)
    {
        if (button.Flyout is { } existing)
        {
            return existing;
        }

        var address = (string?)button.Tag;
        var menu = new SearchableItemsFlyout
        {
            FilterBoxAutomationName = address is null ? "Filter root locations" : $"Filter children of {address}",
            FilterPlaceholderText = "Filter locations"
        };
        AutomationProperties.SetName(menu, address is null ? "Root locations" : $"Children of {address}");
        if (FindVisualChild<ListView>(menu.Content) is { } list)
        {
            list.ContainerContentChanging += static (_, args) => ToolTipService.SetToolTip(
                args.ItemContainer, args.InRecycleQueue ? null : AutomationProperties.GetHelpText(args.ItemContainer));
        }

        menu.ItemInvoked += (_, args) => this.RequestNavigation((OmniboxItem)args.InvokedItem.Tag!);
        menu.Opening += async (_, _) => await this.LoadChildrenAsync(menu, address);
        menu.Closing += (_, _) =>
        {
            if (ReferenceEquals(this._childrenMenu, menu))
            {
                this._childrenMenu = null;
                this._childrenCancellation?.Cancel();
                this._childrenCancellation = null;
            }
        };
        menu.Closed += (_, _) => menu.ItemsSource = null;
        button.Flyout = menu;
        return menu;
    }

    private void CloseChildrenMenu()
    {
        this._childrenMenu?.Hide();
    }

    private async Task LoadChildrenAsync(SearchableItemsFlyout menu, string? address)
    {
        this.CloseChildrenMenu();
        this.OverflowMenu.Hide();
        this._childrenMenu = menu;
        menu.NoResultsText = "Loading locations…";
        // shortcut: Preview 2 sizes the popup only at open. Reserve the list and filter height until the toolkit resizes after source changes.
        ((FrameworkElement)menu.Content).Height = menu.MaxListHeight + 48;
        using var cancellation = new CancellationTokenSource();
        this._childrenCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var items = this.ChildrenProvider is { } provider ? await provider(address, token) : [];
            token.ThrowIfCancellationRequested();
            menu.NoResultsText = items.Count == 0 ? "No child locations" : "No matching locations";
            menu.ItemsSource = items.Select(item => new SearchableFlyoutItem
            {
                Text = item.Label,
                AutomationHelpText = item.Address,
                Tag = item
            }).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            menu.NoResultsText = $"Could not load locations: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(this._childrenCancellation, cancellation))
            {
                this._childrenCancellation = null;
            }
        }
    }

    private void DisplayPath_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var buttons = new List<Button> { this.RootButton, this.OverflowButton };
        foreach (var segment in this._segments.Skip(this._firstVisible))
        {
            buttons.Add(segment.Label);
            buttons.Add(segment.Children);
        }

        buttons.Add(this.EditAddressButton);
        buttons.RemoveAll(button => button.Visibility != Visibility.Visible || !button.IsEnabled);
        var focused = FocusManager.GetFocusedElement(this.XamlRoot);
        var index = buttons.FindIndex(button => ReferenceEquals(button, focused));
        if (index < 0)
        {
            return;
        }

        if (args.Key is VirtualKey.Left or VirtualKey.Right)
        {
            var offset = args.Key == VirtualKey.Right == (this.FlowDirection == FlowDirection.LeftToRight) ? 1 : -1;
            buttons[Math.Clamp(index + offset, 0, buttons.Count - 1)].Focus(FocusState.Keyboard);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Down && !ReferenceEquals(focused, this.EditAddressButton))
        {
            var button = this._segments.FirstOrDefault(segment => ReferenceEquals(segment.Label, focused)).Children ?? buttons[index];
            if (button.Visibility == Visibility.Visible)
            {
                this.GetBreadcrumbMenu(button).ShowAt(button);
                args.Handled = true;
            }
        }
    }

    public static Visibility DescriptionVisibility(string? detail)
    {
        return string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static Visibility AddressEditorIconVisibility(Visibility editor, Visibility scopeToken) =>
        scopeToken == Visibility.Visible ? Visibility.Collapsed : editor;

    public static string InlineDetail(string detail)
    {
        return detail.Length == 0 ? string.Empty : "  ·  " + detail;
    }

    public static string? SuggestionToolTip(string label, string detail)
    {
        return detail.Length == 0 ? null : $"{label}\n{detail}";
    }

    internal static string ShowMoreName(string label) => $"Show more from {label}";

    internal static string SuggestionActionsName(string label) => $"More actions for {label}";

    private void UpdateLocationIcon()
    {
        // Recreate the presenter so clearing and restoring an IconSource is safe.
        var icon = this.LocationIconSource is { } source ? new IconSourceElement { IconSource = source } : null;
        if (icon is not null)
        {
            AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        }

        this.LocationIcon.Content = icon;
        this.LocationIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EditAddress_Click(object sender, RoutedEventArgs args)
    {
        this.BeginEdit();
    }

    private void DisplayPath_Tapped(object sender, TappedRoutedEventArgs args)
    {
        for (var element = args.OriginalSource as DependencyObject;
             element is not null && element != this.DisplayPath;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase)
            {
                return;
            }
        }

        this.BeginEdit();
        args.Handled = true;
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs args)
    {
        var package = new DataPackage();
        package.SetText(this.Address);
        Clipboard.SetContent(package);
    }

    private static bool Contains(DependencyObject parent, DependencyObject? child)
    {
        for (; child is not null; child = VisualTreeHelper.GetParent(child))
        {
            if (child == parent)
            {
                return true;
            }
        }

        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null) where T : FrameworkElement
    {
        if (parent is T element && (name is null || element.Name == name))
        {
            return element;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindVisualChild<T>(VisualTreeHelper.GetChild(parent, i), name) is { } child)
            {
                return child;
            }
        }

        return null;
    }
}

[WinRT.GeneratedWinRTExposedType]
internal sealed partial class OmniboxSuggestionGroup(string key) : ObservableCollection<OmniboxItem>
{
    private string _label = key;

    internal string Key { get; } = key;

    public string Label
    {
        get => this._label;
        set
        {
            if (this._label != value)
            {
                this._label = value;
                this.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Label)));
            }
        }
    }
}

internal sealed partial class OmniboxSuggestionTemplateSelector : DataTemplateSelector
{
    public DataTemplate Result { get; set; } = null!;

    public DataTemplate PreviewHeader { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item)
    {
        return item is OmniboxItem { FirstHiddenResult: not null } ? this.PreviewHeader : this.Result;
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        this.SelectTemplateCore(item);
}
