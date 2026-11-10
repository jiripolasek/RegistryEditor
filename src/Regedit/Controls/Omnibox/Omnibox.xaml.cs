// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace JPSoftworks.Regedit.Controls.Omnibox;

public sealed partial class Omnibox : UserControl
{
    private CancellationTokenSource? _suggestionsCancellation;
    private CancellationTokenSource? _childrenCancellation;
    private Flyout? _childrenMenu;
    private Func<string?, CancellationToken, Task<IReadOnlyList<OmniboxItem>>>? _childrenProvider;
    private readonly List<(OmniboxItem Item, StackPanel Panel, Button Label, Button Children)> _segments = [];
    private int _firstVisible;
    private bool _isEnteringEditMode;

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

    public event EventHandler<OmniboxNavigationRequestedEventArgs>? NavigationRequested;

    public UIElement? TrailingContent
    {
        get => this.TrailingPresenter.Content as UIElement;
        set => this.TrailingPresenter.Content = value;
    }

    public Omnibox()
    {
        this.InitializeComponent();
        this.RootButton.Flyout = this.CreateChildrenMenu(null);
        this.Loaded += (_, _) => this.UpdateOverflow();
        this.RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => this.UpdateOverflow());
        this.Unloaded += (_, _) => this.EndEdit(false);
    }

    public void SetLocation(string address, IEnumerable<OmniboxItem> breadcrumbs)
    {
        var restoreFocus = this.XamlRoot is not null && Contains(
            this, FocusManager.GetFocusedElement(this.XamlRoot) as DependencyObject);
        this.Breadcrumbs = breadcrumbs;
        this.Address = address;
        this.EndEdit(restoreFocus);
    }

    public void BeginEdit()
    {
        this._isEnteringEditMode = true;
        var contextMenu = ((FrameworkElement)this.Content).ContextFlyout;
        var menu = this._childrenMenu ?? (this.OverflowMenu.IsOpen ? this.OverflowMenu : contextMenu);
        if (menu is { IsOpen: true })
        {
            // Flyouts restore focus when they close, so focus the editor afterward.
            void OnClosed(object? sender, object args)
            {
                menu.Closed -= OnClosed;
                this.DispatcherQueue.TryEnqueue(this.FocusEditor);
            }

            menu.Closed += OnClosed;
        }
        else
        {
            this.DispatcherQueue.TryEnqueue(this.FocusEditor);
        }

        this.CloseChildrenMenu();
        this.OverflowMenu.Hide();
        contextMenu?.Hide();
        this.CancelSuggestions();
        this.Editor.Text = this.Address;
        this.Editor.MinHeight = this.ActualHeight;
        this.DisplayPath.Visibility = Visibility.Collapsed;
        this.Editor.Visibility = Visibility.Visible;
    }

    private void FocusEditor()
    {
        if (this.Editor.Visibility != Visibility.Visible)
        {
            return;
        }

        // The newly visible editor must finish layout before its inner textbox can receive focus.
        this.Editor.UpdateLayout();
        if (FindTextBox(this.Editor) is { } textBox && textBox.Focus(FocusState.Keyboard))
        {
            textBox.SelectAll();
            this._isEnteringEditMode = false;
        }
    }

    private void EndEdit(bool restoreFocus)
    {
        this._isEnteringEditMode = false;
        this.CloseChildrenMenu();
        this.OverflowMenu.Hide();
        this.CancelSuggestions();
        this.Editor.Text = this.Address;
        this.Editor.Visibility = Visibility.Collapsed;
        this.DisplayPath.Visibility = Visibility.Visible;
        if (restoreFocus)
        {
            this.EditAddressButton.Focus(FocusState.Programmatic);
        }
    }

    private void CancelSuggestions()
    {
        this._suggestionsCancellation?.Cancel();
        this._suggestionsCancellation = null;
        this.Editor.ItemsSource = null;
        this.Editor.IsSuggestionListOpen = false;
    }

    private async void Editor_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || this.Editor.Visibility != Visibility.Visible)
        {
            return;
        }

        if (this.SuggestionProvider is not { } provider)
        {
            this.CancelSuggestions();
            return;
        }

        // Keep the current list visible while the replacement request is pending.
        this._suggestionsCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        this._suggestionsCancellation = cancellation;
        var token = cancellation.Token;
        var text = sender.Text;
        try
        {
            await Task.Delay(120, token);
            var suggestions = await provider(text, token);
            token.ThrowIfCancellationRequested();
            if (this.Editor.Visibility != Visibility.Visible || sender.Text != text)
            {
                return;
            }

            sender.ItemsSource = suggestions;
            sender.IsSuggestionListOpen = suggestions.Count > 0;
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
        if (args.SelectedItem is OmniboxItem item)
        {
            sender.Text = item.Address;
        }
    }

    private void Editor_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var address = args.ChosenSuggestion is OmniboxItem item ? item.Address : args.QueryText;
        this.CancelSuggestions();
        if (!string.IsNullOrWhiteSpace(address))
        {
            this.NavigationRequested?.Invoke(this, new OmniboxNavigationRequestedEventArgs(address));
        }
    }

    private void Editor_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape)
        {
            return;
        }

        this.EndEdit(true);
        args.Handled = true;
    }

    private void Editor_LostFocus(object sender, RoutedEventArgs args)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            if (!this._isEnteringEditMode && this.XamlRoot is not null &&
                this.Editor.Visibility == Visibility.Visible && !this.Editor.IsSuggestionListOpen && !Contains(
                    this, FocusManager.GetFocusedElement(this.XamlRoot) as DependencyObject))
            {
                this.EndEdit(false);
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
                Flyout = this.CreateChildrenMenu(item.Address),
                Visibility = this.ChildrenProvider is null || item.HasChildren == false
                    ? Visibility.Collapsed
                    : Visibility.Visible
            };
            AutomationProperties.SetName(children, $"Children of {item.Label}");
            AutomationProperties.SetAutomationId(children, $"BreadcrumbChildren{this._segments.Count}");
            ToolTipService.SetToolTip(children, $"Children of {item.Label}");
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(label);
            panel.Children.Add(children);
            if (this.ChildrenProvider is null && this._segments.Count > 0)
            {
                panel.Children.Insert(
                    0, new FontIcon { Glyph = "\uE76C", FontSize = 10, Margin = new Thickness(2, 0, 2, 0) });
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

        this._firstVisible = OmniboxLayout.FirstVisible(
            widths, this.TrailViewport.ActualWidth, this.OverflowButton.Width);
        this.OverflowButton.Visibility = this._firstVisible > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < this._firstVisible; i++)
        {
            this._segments[i].Panel.Visibility = Visibility.Collapsed;
        }

        var last = this._segments[^1];
        var preceding = widths.Skip(this._firstVisible).Take(widths.Length - this._firstVisible - 1).Sum();
        var available = this.TrailViewport.ActualWidth - (this._firstVisible > 0 ? this.OverflowButton.Width : 0) -
                        preceding;
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

    private Flyout CreateChildrenMenu(string? address)
    {
        // A finite viewport keeps large child collections virtualized.
        var list = new ListView
        {
            MaxHeight = 320,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            ItemTemplate = (DataTemplate)this.Resources["ChildLocationTemplate"],
            ItemContainerStyle = (Style)this.Resources["ChildLocationContainerStyle"]
        };
        AutomationProperties.SetAutomationId(list, "ChildLocations");
        AutomationProperties.SetName(list, address is null ? "Root locations" : $"Children of {address}");
        var status = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "ChildLocationsStatus");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var content = new Grid { MinWidth = 160, MaxWidth = 480 };
        content.Children.Add(list);
        content.Children.Add(status);
        var menu = new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            FlyoutPresenterStyle = (Style)this.Resources["ChildLocationsFlyoutStyle"]
        };
        list.ItemClick += (_, args) =>
        {
            this.CloseChildrenMenu();
            this.RequestNavigation((OmniboxItem)args.ClickedItem);
        };
        menu.Opening += async (_, _) => await this.LoadChildrenAsync(menu, list, status, address);
        // Cancel before the closing animation so an older close cannot cancel a reopened menu.
        menu.Closing += (_, _) =>
        {
            if (ReferenceEquals(this._childrenMenu, menu))
            {
                this.CloseChildrenMenu();
            }

            list.ItemsSource = null;
        };
        return menu;
    }

    private void CloseChildrenMenu()
    {
        this._childrenCancellation?.Cancel();
        this._childrenCancellation = null;
        var menu = this._childrenMenu;
        this._childrenMenu = null;
        menu?.Hide();
    }

    private async Task LoadChildrenAsync(Flyout menu, ListView list, TextBlock status, string? address)
    {
        this.CloseChildrenMenu();
        this.OverflowMenu.Hide();
        this._childrenMenu = menu;
        using var cancellation = new CancellationTokenSource();
        this._childrenCancellation = cancellation;
        var token = cancellation.Token;
        list.ItemsSource = null;
        list.Visibility = Visibility.Collapsed;
        status.Text = "Loading locations…";
        status.Visibility = Visibility.Visible;
        AutomationProperties.SetHelpText(status, string.Empty);
        ToolTipService.SetToolTip(status, null);
        try
        {
            var items = this.ChildrenProvider is { } provider ? await provider(address, token) : [];
            token.ThrowIfCancellationRequested();
            list.ItemsSource = items;
            list.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            status.Text = "No child locations";
            status.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (!token.IsCancellationRequested && ReferenceEquals(this._childrenMenu, menu) && items.Count > 0)
                {
                    list.UpdateLayout();
                    if (list.ContainerFromIndex(0) is ListViewItem first)
                    {
                        first.Focus(FocusState.Programmatic);
                    }
                }
            });
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

            list.ItemsSource = null;
            list.Visibility = Visibility.Collapsed;
            status.Text = "Could not load locations";
            status.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(status, ex.Message);
            AutomationProperties.SetHelpText(status, ex.Message);
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
        else if (args.Key == VirtualKey.Down)
        {
            var button = buttons[index];
            if (button.Flyout is null)
            {
                button = this._segments.FirstOrDefault(segment => ReferenceEquals(segment.Label, focused)).Children;
            }

            if (button?.Flyout is { } flyout && button.Visibility == Visibility.Visible)
            {
                flyout.ShowAt(button);
                args.Handled = true;
            }
        }
    }

    public static Visibility DescriptionVisibility(string label, string address)
    {
        return label == address ? Visibility.Collapsed : Visibility.Visible;
    }

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

    private static TextBox? FindTextBox(DependencyObject parent)
    {
        if (parent is TextBox textBox)
        {
            return textBox;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindTextBox(VisualTreeHelper.GetChild(parent, i)) is { } child)
            {
                return child;
            }
        }

        return null;
    }
}
