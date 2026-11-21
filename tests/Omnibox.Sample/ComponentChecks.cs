// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using System.Text.Json;
using JPSoftworks.Regedit;
using JPSoftworks.Regedit.Controls.Omnibox;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using AddressControl = JPSoftworks.Regedit.Controls.Omnibox.Omnibox;

namespace JPSoftworks.Omnibox.Sample;

internal static class ComponentChecks
{
    internal static async Task RunAsync(AddressControl first, AddressControl second, StackPanel parent, string output)
    {
        var passed = new List<string>();
        object? largeList = null;
        string? failure = null;

        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name);
            }

            passed.Add(name);
        }

        try
        {
            await Task.Delay(100);
            var fresh = App.CreateAddressControl();
            fresh.SetLocation(App.Projects.Address, App.Paths[App.Projects.Address]);
            parent.Children.Insert(0, fresh);
            fresh.BeginEdit();
            await Task.Delay(150);
            Check(
                FocusManager.GetFocusedElement(fresh.XamlRoot) is TextBox
                {
                    SelectionStart: 0
                } freshInput
                && freshInput.SelectedText == fresh.Address,
                "Editing before the first layout focuses the textbox and selects the full address");
            parent.Children.Remove(fresh);
            foreach (var entry in new[] { "shortcut", "button", "context menu" })
            {
                if (entry == "shortcut")
                {
                    first.BeginEdit();
                }
                else if (entry == "button")
                {
                    var editButton = Button(first, "EditAddressButton");
                    editButton.Focus(FocusState.Keyboard);
                    Invoke(editButton);
                }
                else
                {
                    var display = (Grid)first.FindName("DisplayPath");
                    var contextMenu = (MenuFlyout)((FrameworkElement)VisualTreeHelper.GetParent(display)).ContextFlyout;
                    contextMenu.ShowAt(first);
                    await WaitAsync(() => contextMenu.IsOpen);
                    Invoke(contextMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "Edit address"));
                }

                await WaitAsync(
                    () => FocusManager.GetFocusedElement(first.XamlRoot) is TextBox focused && focused.SelectedText == first.Address,
                    () => $"Editing from {entry}: focus={FocusManager.GetFocusedElement(first.XamlRoot)}, "
                          + $"editor={((AutoSuggestBox)first.FindName("Editor")).Visibility}");
                await Task.Delay(50);
                Check(
                    FocusManager.GetFocusedElement(first.XamlRoot) is TextBox inputBox && inputBox.SelectedText == first.Address,
                    $"Editing from the {entry} focuses the textbox and selects the full address");
                first.SetLocation(App.Component.Address, App.Paths[App.Component.Address]);
            }

            first.BeginEdit();
            first.SetLocation(App.Component.Address, App.Paths[App.Component.Address]);
            var otherButton = Button(second, "EditAddressButton");
            otherButton.Focus(FocusState.Keyboard);
            await Task.Delay(150);
            Check(
                ReferenceEquals(FocusManager.GetFocusedElement(first.XamlRoot), otherButton),
                "Leaving edit mode before queued focus runs does not steal focus");
            Check(first.Address == App.Component.Address && second.Address == App.Notes.Address, "Independent opaque addresses");
            var locationIcon = (ContentPresenter)first.FindName("LocationIcon");
            Check(locationIcon.Visibility == Visibility.Collapsed, "An omitted location icon consumes no breadcrumb space");
            first.LocationIconSource = new SymbolIconSource { Symbol = Symbol.Home };
            await WaitAsync(() => locationIcon.Visibility == Visibility.Visible
                                  && locationIcon.Content is IconSourceElement
                                  {
                                      IconSource: SymbolIconSource { Symbol: Symbol.Home }
                                  });
            Check(
                ((ContentPresenter)second.FindName("LocationIcon")).Visibility == Visibility.Collapsed && !locationIcon.IsHitTestVisible,
                "A host-supplied location icon is decorative and belongs to its own instance");
            first.LocationIconSource = new FontIconSource { Glyph = "\uE8B7" };
            await WaitAsync(() => locationIcon.Content is IconSourceElement
            {
                IconSource: FontIconSource { Glyph: "\uE8B7" }
            });
            Check(first.Address == App.Component.Address, "Changing the location icon updates the presenter without navigating");
            first.LocationIconSource = null;
            await WaitAsync(() => locationIcon.Visibility == Visibility.Collapsed);
            Check(locationIcon.Content is null, "Clearing the location icon removes its content and spacing");
            Check(
                Button(first, "BreadcrumbChildren2").Visibility == Visibility.Collapsed
                && Button(second, "BreadcrumbChildren1").Visibility == Visibility.Collapsed, "Known leaves have no trailing chevron");
            Check(
                Button(first, "BreadcrumbChildren0").Visibility == Visibility.Visible
                && Button(first, "BreadcrumbChildren1").Visibility == Visibility.Visible, "Ancestor chevrons remain available");
            first.IsRootSelectorVisible = false;
            Check(
                Button(first, "RootButton").Visibility == Visibility.Collapsed
                && Button(first, "BreadcrumbChildren0").Visibility == Visibility.Visible
                && Button(second, "RootButton").Visibility == Visibility.Visible,
                "Hosts can hide a duplicate root selector without changing child navigation or other instances");
            first.IsRootSelectorVisible = true;
            Check(Button(first, "RootButton").Visibility == Visibility.Visible, "The standalone root selector can be restored");
            var requests = 0;
            first.NavigationRequested += (_, _) => requests++;
            var root = Button(first, "RootButton");
            var menu = (Flyout)root.Flyout;
            menu.ShowAt(root);
            await WaitAsync(() => Locations(menu).Items.OfType<OmniboxItem>().Any(item => item.Label == "Workspace"));
            Check(requests == 0, "Opening roots does not navigate");
            await CloseAsync(menu);

            var childButton = Button(first, "BreadcrumbChildren0");
            menu = (Flyout)childButton.Flyout;
            menu.ShowAt(childButton);
            await WaitAsync(() => Locations(menu).ContainerFromIndex(0) is ListViewItem);
            Check(requests == 0, "Opening children does not navigate");
            Invoke((ListViewItem)Locations(menu).ContainerFromItem(App.Projects));
            await WaitAsync(() => first.Address == App.Projects.Address);
            Check(requests == 1 && second.Address == App.Notes.Address, "Selecting a child navigates only its own instance");
            Check(
                Button(first, "BreadcrumbChildren1").Visibility == Visibility.Visible,
                "A current location with children keeps its trailing chevron");
            Invoke(Button(first, "Breadcrumb1"));
            await Task.Delay(30);
            Check(requests == 1, "The current breadcrumb does not request navigation");

            first.SetLocation(App.Notes.Address, (OmniboxItem[])[App.Workspace, App.Notes with { HasChildren = null }]);
            childButton = Button(first, "BreadcrumbChildren1");
            Check(childButton.Visibility == Visibility.Visible, "Unknown child availability preserves on-demand discovery");
            menu = (Flyout)childButton.Flyout;
            menu.ShowAt(childButton);
            await WaitAsync(() => Status(menu).Text == "No child locations");
            Check(
                Locations(menu).Items.Count == 0 && Locations(menu).Visibility == Visibility.Collapsed,
                "Empty menus do not offer a navigation target");
            await CloseAsync(menu);

            first.ChildrenProvider = (_, _) => throw new IOException("Provider unavailable");
            childButton = Button(first, "BreadcrumbChildren0");
            menu = (Flyout)childButton.Flyout;
            menu.ShowAt(childButton);
            await WaitAsync(() => Status(menu).Text == "Could not load locations");
            Check(AutomationProperties.GetHelpText(Status(menu)) == "Provider unavailable", "Provider failures remain accessible");
            await CloseAsync(menu);

            var pending = new TaskCompletionSource<IReadOnlyList<OmniboxItem>>();
            CancellationToken pendingToken = default;
            first.ChildrenProvider = (_, token) =>
            {
                pendingToken = token;
                return pending.Task;
            };
            childButton = Button(first, "BreadcrumbChildren0");
            menu = (Flyout)childButton.Flyout;
            menu.ShowAt(childButton);
            await WaitAsync(() => menu.IsOpen);
            Check(Status(menu).Text == "Loading locations…" && Locations(menu).Items.Count == 0, "Pending providers display loading");
            first.BeginEdit();
            await WaitAsync(() => !menu.IsOpen);
            Check(pendingToken.IsCancellationRequested, "Editing cancels an open child request");
            pending.SetResult((OmniboxItem[])[new OmniboxItem("Stale result", "stale:")]);
            await Task.Delay(50);
            Check(!menu.IsOpen && Locations(menu).Items.Count == 0, "Late results cannot reopen or replace a closed menu");
            first.SetLocation(App.Component.Address, App.Paths[App.Component.Address]);

            var older = new TaskCompletionSource<IReadOnlyList<OmniboxItem>>();
            var call = 0;
            first.ChildrenProvider = (_, _) =>
                ++call == 1 ? older.Task : Task.FromResult<IReadOnlyList<OmniboxItem>>((OmniboxItem[])[App.Projects]);
            childButton = Button(first, "BreadcrumbChildren0");
            menu = (Flyout)childButton.Flyout;
            menu.ShowAt(childButton);
            await WaitAsync(() => menu.IsOpen);
            await CloseAsync(menu);
            menu.ShowAt(childButton);
            await WaitAsync(
                () => Locations(menu).Items.OfType<OmniboxItem>().Any(item => item.Label == "Projects"),
                () => $"Reopen requests: {call}; open: {menu.IsOpen}; items: {string.Join(", ", Locations(menu).Items)}");
            older.SetResult((OmniboxItem[])[new OmniboxItem("Older request", "old:")]);
            await Task.Delay(50);
            Check(Locations(menu).Items.OfType<OmniboxItem>().Single().Label == "Projects", "Reopening a menu rejects an older completion");
            await CloseAsync(menu);

            var manyItems = await Task.Run(() => Enumerable.Range(0, 10_000)
                .Select(index => new OmniboxItem($"Location {index:D5}", $"large:{index}")).ToArray());
            first.ChildrenProvider = (_, _) => Task.FromResult<IReadOnlyList<OmniboxItem>>(manyItems);
            childButton = Button(first, "BreadcrumbChildren0");
            menu = (Flyout)childButton.Flyout;
            var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            var watch = Stopwatch.StartNew();
            var lastTick = 0d;
            var longestPause = 0d;
            heartbeat.Tick += (_, _) =>
            {
                var now = watch.Elapsed.TotalMilliseconds;
                longestPause = Math.Max(longestPause, now - lastTick);
                lastTick = now;
            };
            heartbeat.Start();
            try
            {
                menu.ShowAt(childButton);
                var list = Locations(menu);
                await WaitAsync(() => list.ContainerFromIndex(0) is ListViewItem);
                var loadMilliseconds = watch.Elapsed.TotalMilliseconds;
                var firstRealized = list.ItemsPanelRoot.Children.Count;
                Check(
                    list.Items.Count == manyItems.Length
                    && firstRealized is > 0 and < 100
                    && list.ContainerFromIndex(manyItems.Length - 1) is null,
                    "Ten thousand children create only viewport containers without truncating results");
                list.ScrollIntoView(manyItems[^1]);
                await WaitAsync(() => list.ContainerFromIndex(manyItems.Length - 1) is ListViewItem);
                var lastRealized = list.ItemsPanelRoot.Children.Count;
                Check(lastRealized < 100, "Scrolling a large popup keeps container creation bounded");
                await WaitAsync(() =>
                    list.ContainerFromIndex(manyItems.Length - 1) is ListViewItem row
                    && Equals(row.Content, manyItems[^1])
                    && FrameworkElementAutomationPeer.CreatePeerForElement(row).GetName() == manyItems[^1].Label);
                var lastContainer = (ListViewItem)list.ContainerFromIndex(manyItems.Length - 1);
                Check(
                    Equals(lastContainer.Content, manyItems[^1])
                    && FrameworkElementAutomationPeer.CreatePeerForElement(lastContainer).GetName() == manyItems[^1].Label,
                    "Recycled rows expose the correct accessible label");
                string? requestedAddress = null;

                void CaptureRequest(object? sender, OmniboxNavigationRequestedEventArgs args)
                {
                    requestedAddress = args.Address;
                }

                first.NavigationRequested += CaptureRequest;
                Invoke(lastContainer);
                await WaitAsync(() => requestedAddress is not null && !menu.IsOpen);
                first.NavigationRequested -= CaptureRequest;
                Check(requestedAddress == manyItems[^1].Address, "The last child remains reachable and navigates using its opaque address");
                Check(list.ItemsSource is null, "Closing a child popup releases its loaded collection");
                await Task.Delay(50);
                largeList = new
                {
                    count = manyItems.Length,
                    firstRealized,
                    lastRealized,
                    loadMilliseconds,
                    longestPause
                };
                Check(longestPause < 1000, "Large popup loading and scrolling keep the UI dispatcher responsive");
            }
            finally
            {
                heartbeat.Stop();
            }

            first.ChildrenProvider = App.ChildrenAsync;
            first.LocationIconSource = new FontIconSource { Glyph = "\uE8B7" };
            first.Width = 240;
            first.UpdateLayout();
            await Task.Delay(50);
            Check(
                locationIcon.Visibility == Visibility.Visible
                && locationIcon.ActualWidth == 16
                && Button(first, "OverflowButton").Visibility == Visibility.Visible
                && ((FrameworkElement)VisualTreeHelper.GetParent(Button(first, "Breadcrumb2"))).Visibility == Visibility.Visible,
                "Narrow fields preserve the current breadcrumb beside its icon and expose ancestors");
            var overflow = Button(first, "OverflowButton");
            var overflowMenu = (MenuFlyout)overflow.Flyout;
            overflowMenu.ShowAt(overflow);
            await WaitAsync(() => overflowMenu.IsOpen);
            Check(
                overflowMenu.Items.OfType<MenuFlyoutItem>().Select(item => item.Text).SequenceEqual(new[] { "Workspace", "Projects" }),
                "Overflow preserves ancestor order");
            Invoke(overflowMenu.Items.OfType<MenuFlyoutItem>().First());
            await WaitAsync(() => first.Address == App.Workspace.Address);
            Check(first.Address == App.Workspace.Address, "Overflow navigation uses the ancestor's opaque address");

            first.ChildrenProvider = null;
            first.SetLocation(App.Projects.Address, App.Paths[App.Projects.Address]);
            Check(
                Button(first, "RootButton").Visibility == Visibility.Collapsed
                && Button(first, "BreadcrumbChildren0").Visibility == Visibility.Collapsed, "Child providers are optional");
            foreach (var trailingHeight in new[] { 36d, 52d, 0d })
            {
                first.TrailingContent = trailingHeight == 0 ? null : new Button { Height = trailingHeight, Content = "Action" };
                first.UpdateLayout();
                var height = first.ActualHeight;
                var parentHeight = parent.DesiredSize.Height;
                first.BeginEdit();
                await Task.Delay(50);
                first.UpdateLayout();
                Check(
                    Math.Abs(first.ActualHeight - height) < 0.1 && Math.Abs(parent.DesiredSize.Height - parentHeight) < 0.1,
                    $"Editing preserves control and parent height with {trailingHeight}px trailing content");
                Check(
                    FocusManager.GetFocusedElement(first.XamlRoot) is TextBox
                    {
                        VerticalContentAlignment: VerticalAlignment.Center
                    }, $"Editor text is vertically centered with {trailingHeight}px trailing content");
                first.SetLocation(App.Projects.Address, App.Paths[App.Projects.Address]);
                first.UpdateLayout();
                Check(
                    Math.Abs(first.ActualHeight - height) < 0.1 && Math.Abs(parent.DesiredSize.Height - parentHeight) < 0.1,
                    $"Leaving editing preserves control and parent height with {trailingHeight}px trailing content");
            }

            first.BeginEdit();
            Check(((AutoSuggestBox)first.FindName("Editor")).Text == App.Projects.Address, "Editing uses the confirmed address");
            first.SetLocation(App.Workspace.Address, App.Paths[App.Workspace.Address]);
            Check(((AutoSuggestBox)first.FindName("Editor")).Visibility == Visibility.Collapsed, "Confirmation returns to breadcrumb mode");

            var suggestionRequests
                = new List<(string Text, CancellationToken Token, TaskCompletionSource<IReadOnlyList<OmniboxItem>> Result)>();
            var originalProvider = first.SuggestionProvider;
            first.SuggestionProvider = (text, token) =>
            {
                var result = new TaskCompletionSource<IReadOnlyList<OmniboxItem>>();
                suggestionRequests.Add((text, token, result));
                return result.Task;
            };
            first.BeginEdit();
            await Task.Delay(50);
            var editor = (AutoSuggestBox)first.FindName("Editor");
            var input = (TextBox)FocusManager.GetFocusedElement(first.XamlRoot);
            input.Text = "p";
            await WaitAsync(() => suggestionRequests.Count == 1);
            suggestionRequests[0].Result.SetResult((OmniboxItem[])[App.Projects, App.Notes]);
            await WaitAsync(
                () => editor.IsSuggestionListOpen,
                () =>
                    $"Suggestions: text={editor.Text}, items={editor.Items.Count}, visibility={editor.Visibility}, focus={FocusManager.GetFocusedElement(first.XamlRoot)?.GetType().Name}");
            var addressIcon = (FontIcon)first.FindName("AddressEditorIcon");
            var addressPadding = input.Padding.Left;
            var editorPopup = (Popup)first.FindName("EditorPopup");
            var editorSurface = (Grid)editorPopup.Child;
            var inputRow = (Grid)VisualTreeHelper.GetParent(input);
            var inputChrome = inputRow.Children.OfType<Border>().Single();
            var suggestionPanel = ((Grid)VisualTreeHelper.GetParent(inputRow)).Children.OfType<Border>().Single();
            var suggestionLayout = (Grid)suggestionPanel.Child;
            var addressList = suggestionLayout.Children.OfType<ListView>().Single();
            await WaitAsync(() => addressList.ContainerFromIndex(0) is ListViewItem { ContentTemplateRoot: Grid },
                () => $"Address rows={addressList.Items.Count}, editor rows={editor.Items.Count}, panel={suggestionPanel.Visibility}, list height={addressList.ActualHeight}");
            Check(editorPopup.IsOpen && editorSurface.Children.Contains(editor),
                "The input and suggestion list share one popup");
            Check(suggestionPanel.Shadow is ThemeShadow && editorSurface.Shadow is null,
                "The suggestions panel casts the shadow");
            var inputOrigin = input.TransformToVisual(first).TransformPoint(default);
            Check(Math.Abs(inputOrigin.X) <= 0.5 && Math.Abs(inputOrigin.Y) <= 0.5 && Math.Abs(input.ActualWidth - first.ActualWidth) <= 0.5,
                $"The popup input covers the address row (origin {inputOrigin}, widths {input.ActualWidth} and {first.ActualWidth})");
            Check(addressList.ItemContainerTransitions is { Count: 0 },
                "Suggestion rows have no collection transitions");
            var addressContainer = (ListViewItem)addressList.ContainerFromIndex(0);
            var addressRow = (Grid)addressContainer.ContentTemplateRoot;
            var labelX = addressRow.Children.OfType<TextBlock>().First().TransformToVisual(input).TransformPoint(default).X;
            var textX = input.BorderThickness.Left + input.Padding.Left;
            var iconX = addressIcon.TransformToVisual(input).TransformPoint(default).X;
            var suggestionIconX = addressRow.Children.OfType<FontIcon>().Single().TransformToVisual(input).TransformPoint(default).X;
            Check(addressIcon.Visibility == Visibility.Visible && !addressIcon.IsHitTestVisible &&
                  Math.Abs(textX - labelX) <= 1 && Math.Abs(iconX - suggestionIconX) <= 1,
                $"The default editor aligns its icon and text with autocomplete (input {iconX},{textX}; row {suggestionIconX},{labelX})");
            var inputBackground = inputChrome.Background;
            var inputBorder = inputChrome.BorderBrush;
            var inputCorners = inputChrome.CornerRadius;
            var clearButton = Button(editorPopup.Child, "ClearInputButton");
            var clearVisibility = clearButton.Visibility;
            Check(clearVisibility == Visibility.Visible, "The popup offers a clear button for the query");
            Check(addressContainer.Focus(FocusState.Keyboard), "A suggestion can take keyboard focus within the editor popup");
            await Task.Delay(50);
            Check(ReferenceEquals(FocusManager.GetFocusedElement(first.XamlRoot), addressContainer) && editorPopup.IsOpen &&
                  suggestionPanel.Visibility == Visibility.Visible && inputOrigin.Equals(input.TransformToVisual(first).TransformPoint(default)) &&
                  Equals(inputBackground, inputChrome.Background) && Equals(inputBorder, inputChrome.BorderBrush) &&
                  inputCorners.Equals(inputChrome.CornerRadius) && clearButton.Visibility == clearVisibility,
                "The input outline, clear button, and position stay steady while a suggestion has focus");
            input.Focus(FocusState.Keyboard);
            await WaitAsync(() => editor.IsSuggestionListOpen);
            var dismissed = false;
            var openCallback = editor.RegisterPropertyChangedCallback(
                AutoSuggestBox.IsSuggestionListOpenProperty, (_, _) => dismissed |= !editor.IsSuggestionListOpen);
            input.Text = "pr";
            await WaitAsync(() => suggestionRequests.Count == 2);
            Check(
                !dismissed && editor.Items.OfType<OmniboxItem>().SequenceEqual((OmniboxItem[])[App.Projects, App.Notes]),
                "Typing preserves the open suggestion list during debounce and loading");
            input.Text = "pro";
            await WaitAsync(() => suggestionRequests.Count == 3);
            Check(suggestionRequests[1].Token.IsCancellationRequested, "Typing cancels the previous suggestion request");
            suggestionRequests[2].Result.SetResult((OmniboxItem[])[App.Projects with { }, App.Component]);
            await WaitAsync(() => editor.Items.OfType<OmniboxItem>().SequenceEqual((OmniboxItem[])[App.Projects, App.Component]));
            suggestionRequests[1].Result.SetResult((OmniboxItem[])[App.Notes]);
            await Task.Delay(50);
            Check(
                !dismissed && editor.Items.OfType<OmniboxItem>().SequenceEqual((OmniboxItem[])[App.Projects, App.Component]),
                "Replacement suggestions stay open and ignore cancelled results");
            Check(ReferenceEquals(editor.Items[0], App.Projects) &&
                  ReferenceEquals(addressContainer, addressList.ContainerFromIndex(0)),
                "Refreshing address suggestions retains unchanged rows and their containers");
            editor.UnregisterPropertyChangedCallback(AutoSuggestBox.IsSuggestionListOpenProperty, openCallback);
            input.Text = "missing";
            await WaitAsync(() => suggestionRequests.Count == 4);
            suggestionRequests[3].Result.SetResult([]);
            await WaitAsync(() => !editor.IsSuggestionListOpen);
            Check(editor.Items.Count == 0, "An empty replacement closes and clears suggestions");
            input.Text = "error";
            await WaitAsync(() => suggestionRequests.Count == 5);
            editor.ItemsSource = new[] { App.Projects };
            editor.IsSuggestionListOpen = true;
            suggestionRequests[4].Result.SetException(new InvalidOperationException("Suggestion failure"));
            await WaitAsync(() => !editor.IsSuggestionListOpen && editor.Items.Count == 0);
            Check(true, "A failed replacement closes and clears suggestions");
            input.Text = "pending";
            await WaitAsync(() => suggestionRequests.Count == 6);
            first.SetLocation(App.Workspace.Address, App.Paths[App.Workspace.Address]);
            suggestionRequests[5].Result.SetResult((OmniboxItem[])[App.Projects]);
            await Task.Delay(50);
            Check(
                suggestionRequests[5].Token.IsCancellationRequested && !editor.IsSuggestionListOpen && editor.Items.Count == 0,
                "Leaving edit mode cancels suggestions and prevents delayed reopening");
            first.SuggestionProvider = originalProvider;

            first.BeginEdit();
            await WaitAsync(() => ReferenceEquals(FocusManager.GetFocusedElement(first.XamlRoot), input));
            input.Text = "#";
            await WaitAsync(() => editor.IsSuggestionListOpen && editor.Items.OfType<OmniboxItem>().Any(item => item.Label == "#pinned"));
            Check(true, "The standalone control suggests the host's custom prefix");
            var popupDismissed = false;
            var panelToken = suggestionPanel.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, (_, _) => popupDismissed |= suggestionPanel.Visibility == Visibility.Collapsed);
            input.Text = "%notes";
            var tokenButton = (Button)first.FindName("ScopeToken");
            await WaitAsync(() => tokenButton.Visibility == Visibility.Visible &&
                                  editorPopup.IsOpen && suggestionPanel.Visibility == Visibility.Visible &&
                                  editor.Items.OfType<OmniboxItem>().SingleOrDefault()?.Address == App.Notes.Address);
            suggestionPanel.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, panelToken);
            Check(!popupDismissed, "Confirming a prefix keeps the same popup open as grouped results replace the prefix");
            Check(editor.PlaceholderText == "Search pinned locations" && input.Text == "notes" && addressIcon.Visibility == Visibility.Collapsed &&
                  input.Padding.Left > tokenButton.ActualWidth, "The alias activates the host's context and keeps the query beside the token");
            var suggestions = suggestionLayout.Children.OfType<ListView>().Single();
            var footer = suggestionLayout.Children.OfType<Border>().Single();
            await WaitAsync(() => suggestionPanel.ActualHeight > 0 && editorPopup.ActualPlacement != PopupPlacementMode.Auto &&
                                  suggestions.Items.Count == 1 && ContainsText(suggestions, "Personal") && ContainsText(footer, "Enter Choose"),
                () => $"Rows={suggestions.Items.Count}, group={ContainsText(suggestions, "Personal")}, footer={ContainsText(footer, "Enter Choose")}");
            Check(true, "Groups and footer leave the suggestion list's item count unchanged");
            var inputPeer = new TextBoxAutomationPeer(input);
            var listPeer = new ListViewAutomationPeer(suggestions);
            var above = listPeer.GetBoundingRectangle().Y < inputPeer.GetBoundingRectangle().Y;
            var rounded = editor.CornerRadius;
            var roundedTop = new CornerRadius(rounded.TopLeft, rounded.TopRight, 0, 0);
            var roundedBottom = new CornerRadius(0, 0, rounded.BottomRight, rounded.BottomLeft);
            Check(rounded.TopLeft > 0 && rounded.TopRight > 0 && rounded.BottomRight > 0 && rounded.BottomLeft > 0 &&
                  inputChrome.CornerRadius.Equals(above ? roundedBottom : roundedTop) &&
                  suggestionPanel.CornerRadius.Equals(above ? roundedTop : roundedBottom),
                $"Only the adjoining input and result corners are square (input {inputChrome.CornerRadius}, results {suggestionPanel.CornerRadius})");
            var popupTop = suggestionPanel.TransformToVisual(input).TransformPoint(default).Y;
            Check(above || Math.Abs(popupTop - input.ActualHeight) <= 0.5,
                $"The dropdown starts below the accent border (popup top {popupTop}, input bottom {input.ActualHeight})");
            editor.IsSuggestionListOpen = false;
            await WaitAsync(() => editorPopup.IsOpen && suggestionPanel.Visibility == Visibility.Collapsed && inputChrome.CornerRadius.Equals(rounded) &&
                                  suggestionPanel.CornerRadius.Equals(rounded),
                () => $"Results {suggestionPanel.Visibility}, input {inputChrome.CornerRadius}, results {suggestionPanel.CornerRadius}");
            Check(true, "Hiding results restores the input's rounded corners");

            input.Text = "e";
            await WaitAsync(() => suggestionPanel.Visibility == Visibility.Visible && Find(suggestions, "OmniboxShowMore") is { ActualHeight: > 0 } &&
                                  ContainsText(suggestions, "Personal"));
            var showMore = Button(suggestions, "OmniboxShowMore");
            var projectsResult = suggestions.Items.OfType<OmniboxItem>().Single(item => item.Address == App.Projects.Address);
            var resultRow = (ListViewItem)suggestions.ContainerFromItem(projectsResult);
            var plainHeader = suggestions.ItemsPanelRoot.Children.OfType<ListViewHeaderItem>()
                .Single(header => ContainsText(header, "Personal"));
            Check(AutomationProperties.GetName(showMore) == "Show more from Work" &&
                  !editor.Items.OfType<OmniboxItem>().Any(item => item.Address == App.Component.Address),
                "Only a group with hidden results has a Show more action");
            Check(resultRow.ActualHeight >= 38 && Math.Abs(plainHeader.ActualHeight - resultRow.ActualHeight) < 0.5 &&
                  Math.Abs(showMore.ActualHeight - resultRow.ActualHeight) < 0.5,
                $"Group header heights match result rows (plain {plainHeader.ActualHeight}, action {showMore.ActualHeight}, result {resultRow.ActualHeight})");
            popupDismissed = false;
            panelToken = suggestionPanel.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, (_, _) => popupDismissed |= suggestionPanel.Visibility == Visibility.Collapsed);
            Invoke(showMore);
            await WaitAsync(() => suggestions.SelectedItem is OmniboxItem item && item.Address == App.Component.Address,
                () => $"Results {suggestionPanel.Visibility}, selected={suggestions.SelectedItem}, " +
                      $"results={string.Join(", ", suggestions.Items.OfType<OmniboxItem>().Select(item => item.Label))}");
            suggestionPanel.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, panelToken);
            Check(!popupDismissed && input.Text == "e" && editor.Items.OfType<OmniboxItem>().All(item => item.FirstHiddenResult is null),
                "Show more keeps the popup and query in place and selects the first revealed result");
            await WaitAsync(() => suggestions.ContainerFromItem(suggestions.SelectedItem) is ListViewItem row &&
                                  VisualTreeHelper.GetChildrenCount(row) > 0);
            var selectedRow = (ListViewItem)suggestions.ContainerFromItem(suggestions.SelectedItem);
            Check(VisualTreeHelper.GetChild(selectedRow, 0) is ListViewItemPresenter { CornerRadius: var rowCorners } &&
                  rowCorners.TopLeft > 0 && rowCorners.TopRight > 0 && rowCorners.BottomRight > 0 && rowCorners.BottomLeft > 0,
                "The selected suggestion has rounded corners");

            input.Text = "o";
            await WaitAsync(() => editor.Items.OfType<OmniboxItem>()
                .Any(item => item.FirstHiddenResult?.Address == App.Component.Address));
            Check(true, "A new query starts from the configured preview again");
            Check(ReferenceEquals(resultRow, suggestions.ContainerFromItem(projectsResult)),
                "Updating a group preview retains the unchanged result container");
            input.Text = "notes";
            await WaitAsync(() => editor.Items.Count == 1 && editor.Items[0] is OmniboxItem item &&
                                  item.Address == App.Notes.Address);
            Invoke(tokenButton);
            await WaitAsync(() => ReferenceEquals(FocusManager.GetFocusedElement(first.XamlRoot), input) &&
                                  tokenButton.Visibility == Visibility.Collapsed && !editor.IsSuggestionListOpen);
            Check(editor.PlaceholderText == first.AddressPlaceholder && input.Text == "notes" &&
                  addressIcon.Visibility == Visibility.Visible && input.Padding.Left == addressPadding,
                "Clearing a context restores the address prompt and keeps the query");
            input.Text = "#pinned";
            await WaitAsync(() => suggestionPanel.Visibility == Visibility.Visible && editor.Items.OfType<OmniboxItem>().Any(item => item.Label == "#pinned"));
            popupDismissed = false;
            panelToken = suggestionPanel.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, (_, _) => popupDismissed |= suggestionPanel.Visibility == Visibility.Collapsed);
            input.Text = "#pinned ";
            await WaitAsync(() => suggestionPanel.Visibility == Visibility.Visible && tokenButton.Visibility == Visibility.Visible &&
                                  input.Text.Length == 0 && editor.Items.OfType<OmniboxItem>().Any(item => item.Address == App.Projects.Address));
            suggestionPanel.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, panelToken);
            Check(!popupDismissed, "Confirming the keyword with Space keeps the popup open with an empty query");
            input.Text = "notes";
            await WaitAsync(() => clearButton.Visibility == Visibility.Visible && editor.Items.Count == 1 &&
                                  suggestions.ContainerFromIndex(0) is ListViewItem);
            ((ListViewItem)suggestions.ContainerFromIndex(0)).Focus(FocusState.Keyboard);
            Invoke(clearButton);
            await WaitAsync(() => input.Text.Length == 0 && clearButton.Visibility == Visibility.Collapsed &&
                                  ReferenceEquals(FocusManager.GetFocusedElement(first.XamlRoot), input));
            Check(true, "Clear resets the query and returns focus from the results to the input");
            first.SetLocation(App.Workspace.Address, App.Paths[App.Workspace.Address]);

            first.IsTextMode = true;
            var addressText = (TextBlock)first.FindName("AddressText");
            var trailViewport = (Grid)first.FindName("TrailViewport");
            Check(
                addressText.Visibility == Visibility.Visible
                && addressText.Text == App.Workspace.Address
                && trailViewport.Visibility == Visibility.Collapsed
                && Button(first, "RootButton").Visibility == Visibility.Collapsed
                && !second.IsTextMode, "Text mode hides breadcrumb controls and preserves independent instances");
            first.BeginEdit();
            Check(((AutoSuggestBox)first.FindName("Editor")).Text == App.Workspace.Address, "Text mode edits the confirmed address");
            first.SetLocation(App.Projects.Address, App.Paths[App.Projects.Address]);
            await WaitAsync(() => addressText.Text == App.Projects.Address);
            Check(
                ((AutoSuggestBox)first.FindName("Editor")).Visibility == Visibility.Collapsed
                && addressText.Visibility == Visibility.Visible
                && trailViewport.Visibility == Visibility.Collapsed,
                "Navigation returns to the updated text path without showing breadcrumbs");
            first.IsTextMode = false;
            Check(
                addressText.Visibility == Visibility.Collapsed
                && trailViewport.Visibility == Visibility.Visible
                && Button(first, "Breadcrumb1") is not null, "Turning text mode off restores the current breadcrumbs");

            pending = new TaskCompletionSource<IReadOnlyList<OmniboxItem>>();
            first.ChildrenProvider = (_, token) =>
            {
                pendingToken = token;
                return pending.Task;
            };
            childButton = Button(first, "BreadcrumbChildren0");
            menu = (Flyout)childButton.Flyout;
            menu.ShowAt(childButton);
            await WaitAsync(() => menu.IsOpen);
            parent.Children.Remove(first);
            await WaitAsync(() => pendingToken.IsCancellationRequested);
            pending.SetResult((OmniboxItem[])[App.Projects]);
            await Task.Delay(50);
            Check(!menu.IsOpen, "Unloading cancels requests and closes the menu");
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(
            output, JsonSerializer.Serialize(new { passed, largeList, failure }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failure is null ? 0 : 1;
    }

    private static ListView Locations(Flyout menu)
    {
        return ((Grid)menu.Content).Children.OfType<ListView>().Single();
    }

    private static bool ContainsText(DependencyObject element, string text)
    {
        if (element is TextBlock block && block.Text.Contains(text, StringComparison.Ordinal))
        {
            return true;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            if (ContainsText(VisualTreeHelper.GetChild(element, index), text))
            {
                return true;
            }
        }

        return false;
    }

    private static TextBlock Status(Flyout menu)
    {
        return ((Grid)menu.Content).Children.OfType<TextBlock>().Single();
    }

    private static Button Button(DependencyObject parent, string id)
    {
        return Find(parent, id) ?? throw new InvalidOperationException($"Button not found: {id}");
    }

    private static Button? Find(DependencyObject parent, string id)
    {
        if (parent is Button button && (button.Name == id || AutomationProperties.GetAutomationId(button) == id))
        {
            return button;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (Find(VisualTreeHelper.GetChild(parent, i), id) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private static void Invoke(FrameworkElement element)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
        if (peer?.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
        {
            throw new InvalidOperationException("The control has no invoke pattern.");
        }

        invoke.Invoke();
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string>? detail = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(detail?.Invoke() ?? "The expected component state did not appear.");
            }

            await Task.Delay(20);
        }
    }

    private static async Task CloseAsync(FlyoutBase menu)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClosed(object? sender, object args)
        {
            menu.Closed -= OnClosed;
            closed.TrySetResult();
        }

        menu.Closed += OnClosed;
        menu.Hide();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
