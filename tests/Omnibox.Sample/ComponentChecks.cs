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
                } freshInput && freshInput.SelectedText == fresh.Address,
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
                    () => FocusManager.GetFocusedElement(first.XamlRoot) is TextBox focused &&
                          focused.SelectedText == first.Address,
                    () => $"Editing from {entry}: focus={FocusManager.GetFocusedElement(first.XamlRoot)}, " +
                          $"editor={((AutoSuggestBox)first.FindName("Editor")).Visibility}");
                await Task.Delay(50);
                Check(
                    FocusManager.GetFocusedElement(first.XamlRoot) is TextBox inputBox &&
                    inputBox.SelectedText == first.Address,
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
            Check(
                first.Address == App.Component.Address && second.Address == App.Notes.Address,
                "Independent opaque addresses");
            var locationIcon = (ContentPresenter)first.FindName("LocationIcon");
            Check(
                locationIcon.Visibility == Visibility.Collapsed,
                "An omitted location icon consumes no breadcrumb space");
            first.LocationIconSource = new SymbolIconSource { Symbol = Symbol.Home };
            await WaitAsync(() => locationIcon.Visibility == Visibility.Visible &&
                                  locationIcon.Content is IconSourceElement
                                  {
                                      IconSource: SymbolIconSource { Symbol: Symbol.Home }
                                  });
            Check(
                ((ContentPresenter)second.FindName("LocationIcon")).Visibility == Visibility.Collapsed &&
                !locationIcon.IsHitTestVisible,
                "A host-supplied location icon is decorative and belongs to its own instance");
            first.LocationIconSource = new FontIconSource { Glyph = "\uE8B7" };
            await WaitAsync(() => locationIcon.Content is IconSourceElement
            {
                IconSource: FontIconSource { Glyph: "\uE8B7" }
            });
            Check(
                first.Address == App.Component.Address,
                "Changing the location icon updates the presenter without navigating");
            first.LocationIconSource = null;
            await WaitAsync(() => locationIcon.Visibility == Visibility.Collapsed);
            Check(locationIcon.Content is null, "Clearing the location icon removes its content and spacing");
            Check(
                Button(first, "BreadcrumbChildren2").Visibility == Visibility.Collapsed &&
                Button(second, "BreadcrumbChildren1").Visibility == Visibility.Collapsed,
                "Known leaves have no trailing chevron");
            Check(
                Button(first, "BreadcrumbChildren0").Visibility == Visibility.Visible &&
                Button(first, "BreadcrumbChildren1").Visibility == Visibility.Visible,
                "Ancestor chevrons remain available");
            first.IsRootSelectorVisible = false;
            Check(
                Button(first, "RootButton").Visibility == Visibility.Collapsed &&
                Button(first, "BreadcrumbChildren0").Visibility == Visibility.Visible &&
                Button(second, "RootButton").Visibility == Visibility.Visible,
                "Hosts can hide a duplicate root selector without changing child navigation or other instances");
            first.IsRootSelectorVisible = true;
            Check(
                Button(first, "RootButton").Visibility == Visibility.Visible,
                "The standalone root selector can be restored");
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
            Check(
                requests == 1 && second.Address == App.Notes.Address,
                "Selecting a child navigates only its own instance");
            Check(
                Button(first, "BreadcrumbChildren1").Visibility == Visibility.Visible,
                "A current location with children keeps its trailing chevron");
            Invoke(Button(first, "Breadcrumb1"));
            await Task.Delay(30);
            Check(requests == 1, "The current breadcrumb does not request navigation");

            first.SetLocation(App.Notes.Address, [App.Workspace, App.Notes with { HasChildren = null }]);
            childButton = Button(first, "BreadcrumbChildren1");
            Check(
                childButton.Visibility == Visibility.Visible,
                "Unknown child availability preserves on-demand discovery");
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
            Check(
                AutomationProperties.GetHelpText(Status(menu)) == "Provider unavailable",
                "Provider failures remain accessible");
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
            Check(
                Status(menu).Text == "Loading locations…" && Locations(menu).Items.Count == 0,
                "Pending providers display loading");
            first.BeginEdit();
            await WaitAsync(() => !menu.IsOpen);
            Check(pendingToken.IsCancellationRequested, "Editing cancels an open child request");
            pending.SetResult([new OmniboxItem("Stale result", "stale:")]);
            await Task.Delay(50);
            Check(
                !menu.IsOpen && Locations(menu).Items.Count == 0,
                "Late results cannot reopen or replace a closed menu");
            first.SetLocation(App.Component.Address, App.Paths[App.Component.Address]);

            var older = new TaskCompletionSource<IReadOnlyList<OmniboxItem>>();
            var call = 0;
            first.ChildrenProvider = (_, _) =>
                ++call == 1 ? older.Task : Task.FromResult<IReadOnlyList<OmniboxItem>>([App.Projects]);
            childButton = Button(first, "BreadcrumbChildren0");
            menu = (Flyout)childButton.Flyout;
            menu.ShowAt(childButton);
            await WaitAsync(() => menu.IsOpen);
            await CloseAsync(menu);
            menu.ShowAt(childButton);
            await WaitAsync(
                () => Locations(menu).Items.OfType<OmniboxItem>().Any(item => item.Label == "Projects"),
                () =>
                    $"Reopen requests: {call}; open: {menu.IsOpen}; items: {string.Join(", ", Locations(menu).Items)}");
            older.SetResult([new OmniboxItem("Older request", "old:")]);
            await Task.Delay(50);
            Check(
                Locations(menu).Items.OfType<OmniboxItem>().Single().Label == "Projects",
                "Reopening a menu rejects an older completion");
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
                    list.Items.Count == manyItems.Length && firstRealized is > 0 and < 100 &&
                    list.ContainerFromIndex(manyItems.Length - 1) is null,
                    "Ten thousand children create only viewport containers without truncating results");
                list.ScrollIntoView(manyItems[^1]);
                await WaitAsync(() => list.ContainerFromIndex(manyItems.Length - 1) is ListViewItem);
                var lastRealized = list.ItemsPanelRoot.Children.Count;
                Check(lastRealized < 100, "Scrolling a large popup keeps container creation bounded");
                await WaitAsync(() => list.ContainerFromIndex(manyItems.Length - 1) is ListViewItem row &&
                                      Equals(row.Content, manyItems[^1]) &&
                                      FrameworkElementAutomationPeer.CreatePeerForElement(row).GetName() ==
                                      manyItems[^1].Label);
                var lastContainer = (ListViewItem)list.ContainerFromIndex(manyItems.Length - 1);
                Check(
                    Equals(lastContainer.Content, manyItems[^1]) &&
                    FrameworkElementAutomationPeer.CreatePeerForElement(lastContainer).GetName() == manyItems[^1].Label,
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
                Check(
                    requestedAddress == manyItems[^1].Address,
                    "The last child remains reachable and navigates using its opaque address");
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
                locationIcon.Visibility == Visibility.Visible && locationIcon.ActualWidth == 16 &&
                Button(first, "OverflowButton").Visibility == Visibility.Visible &&
                ((FrameworkElement)VisualTreeHelper.GetParent(Button(first, "Breadcrumb2"))).Visibility ==
                Visibility.Visible,
                "Narrow fields preserve the current breadcrumb beside its icon and expose ancestors");
            var overflow = Button(first, "OverflowButton");
            var overflowMenu = (MenuFlyout)overflow.Flyout;
            overflowMenu.ShowAt(overflow);
            await WaitAsync(() => overflowMenu.IsOpen);
            Check(
                overflowMenu.Items.OfType<MenuFlyoutItem>().Select(item => item.Text)
                    .SequenceEqual(new[] { "Workspace", "Projects" }), "Overflow preserves ancestor order");
            Invoke(overflowMenu.Items.OfType<MenuFlyoutItem>().First());
            await WaitAsync(() => first.Address == App.Workspace.Address);
            Check(first.Address == App.Workspace.Address, "Overflow navigation uses the ancestor's opaque address");

            first.ChildrenProvider = null;
            first.SetLocation(App.Projects.Address, App.Paths[App.Projects.Address]);
            Check(
                Button(first, "RootButton").Visibility == Visibility.Collapsed &&
                Button(first, "BreadcrumbChildren0").Visibility == Visibility.Collapsed,
                "Child providers are optional");
            foreach (var trailingHeight in new[] { 36d, 52d, 0d })
            {
                first.TrailingContent = trailingHeight == 0
                    ? null
                    : new Button { Height = trailingHeight, Content = "Action" };
                first.UpdateLayout();
                var height = first.ActualHeight;
                var parentHeight = parent.DesiredSize.Height;
                first.BeginEdit();
                await Task.Delay(50);
                first.UpdateLayout();
                Check(
                    Math.Abs(first.ActualHeight - height) < 0.1 &&
                    Math.Abs(parent.DesiredSize.Height - parentHeight) < 0.1,
                    $"Editing preserves control and parent height with {trailingHeight}px trailing content");
                Check(
                    FocusManager.GetFocusedElement(first.XamlRoot) is TextBox
                    {
                        VerticalContentAlignment: VerticalAlignment.Center
                    }, $"Editor text is vertically centered with {trailingHeight}px trailing content");
                first.SetLocation(App.Projects.Address, App.Paths[App.Projects.Address]);
                first.UpdateLayout();
                Check(
                    Math.Abs(first.ActualHeight - height) < 0.1 &&
                    Math.Abs(parent.DesiredSize.Height - parentHeight) < 0.1,
                    $"Leaving editing preserves control and parent height with {trailingHeight}px trailing content");
            }

            first.BeginEdit();
            Check(
                ((AutoSuggestBox)first.FindName("Editor")).Text == App.Projects.Address,
                "Editing uses the confirmed address");
            first.SetLocation(App.Workspace.Address, App.Paths[App.Workspace.Address]);
            Check(
                ((AutoSuggestBox)first.FindName("Editor")).Visibility == Visibility.Collapsed,
                "Confirmation returns to breadcrumb mode");

            var suggestionRequests
                = new List<(string Text, CancellationToken Token, TaskCompletionSource<IReadOnlyList<OmniboxItem>>
                    Result)>();
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
            suggestionRequests[0].Result.SetResult([App.Projects]);
            await WaitAsync(
                () => editor.IsSuggestionListOpen,
                () =>
                    $"Suggestions: text={editor.Text}, items={editor.Items.Count}, visibility={editor.Visibility}, focus={FocusManager.GetFocusedElement(first.XamlRoot)?.GetType().Name}");
            var dismissed = false;
            var openCallback = editor.RegisterPropertyChangedCallback(
                AutoSuggestBox.IsSuggestionListOpenProperty, (_, _) => dismissed |= !editor.IsSuggestionListOpen);
            input.Text = "pr";
            await WaitAsync(() => suggestionRequests.Count == 2);
            Check(
                !dismissed && editor.Items.OfType<OmniboxItem>().SequenceEqual([App.Projects]),
                "Typing preserves the open suggestion list during debounce and loading");
            input.Text = "pro";
            await WaitAsync(() => suggestionRequests.Count == 3);
            Check(
                suggestionRequests[1].Token.IsCancellationRequested, "Typing cancels the previous suggestion request");
            suggestionRequests[2].Result.SetResult([App.Component]);
            await WaitAsync(() => editor.Items.OfType<OmniboxItem>().SequenceEqual([App.Component]));
            suggestionRequests[1].Result.SetResult([App.Notes]);
            await Task.Delay(50);
            Check(
                !dismissed && editor.Items.OfType<OmniboxItem>().SequenceEqual([App.Component]),
                "Replacement suggestions stay open and ignore cancelled results");
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
            suggestionRequests[5].Result.SetResult([App.Projects]);
            await Task.Delay(50);
            Check(
                suggestionRequests[5].Token.IsCancellationRequested && !editor.IsSuggestionListOpen &&
                editor.Items.Count == 0, "Leaving edit mode cancels suggestions and prevents delayed reopening");
            first.SuggestionProvider = originalProvider;

            first.IsTextMode = true;
            var addressText = (TextBlock)first.FindName("AddressText");
            var trailViewport = (Grid)first.FindName("TrailViewport");
            Check(
                addressText.Visibility == Visibility.Visible && addressText.Text == App.Workspace.Address &&
                trailViewport.Visibility == Visibility.Collapsed &&
                Button(first, "RootButton").Visibility == Visibility.Collapsed && !second.IsTextMode,
                "Text mode hides breadcrumb controls and preserves independent instances");
            first.BeginEdit();
            Check(
                ((AutoSuggestBox)first.FindName("Editor")).Text == App.Workspace.Address,
                "Text mode edits the confirmed address");
            first.SetLocation(App.Projects.Address, App.Paths[App.Projects.Address]);
            await WaitAsync(() => addressText.Text == App.Projects.Address);
            Check(
                ((AutoSuggestBox)first.FindName("Editor")).Visibility == Visibility.Collapsed &&
                addressText.Visibility == Visibility.Visible && trailViewport.Visibility == Visibility.Collapsed,
                "Navigation returns to the updated text path without showing breadcrumbs");
            first.IsTextMode = false;
            Check(
                addressText.Visibility == Visibility.Collapsed && trailViewport.Visibility == Visibility.Visible &&
                Button(first, "Breadcrumb1") is not null, "Turning text mode off restores the current breadcrumbs");

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
            pending.SetResult([App.Projects]);
            await Task.Delay(50);
            Check(!menu.IsOpen, "Unloading cancels requests and closes the menu");
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(
            output,
            JsonSerializer.Serialize(
                new { passed, largeList, failure }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = failure is null ? 0 : 1;
    }

    private static ListView Locations(Flyout menu)
    {
        return ((Grid)menu.Content).Children.OfType<ListView>().Single();
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
