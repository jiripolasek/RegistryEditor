// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Infrastructure.Input;
using JPSoftworks.Regedit.ViewModels.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.System;

namespace JPSoftworks.Regedit.Controls.Browser;

public sealed partial class RegistryTreePane : UserControl
{
    private BrowserViewModel ViewModel { get; set; } = null!;

    private int _treeRequest;
    private int _treeGeneration;
    private readonly Dictionary<TreeViewNode, Task<TreeViewNode>> _treeLoads = [];
    private bool _autoSyncTree = true;
    private bool _expandInitialKey = true;
    private Action? _restoreEditViewport;
    private int _viewportVersion;
    private RegistryLocation? _treeRestoreLocation;
    private string? _treeMachine;
    private RegistryView _treeView;

    internal event Action<RegistryLocation>? OpenInBackground;

    internal event Action<Exception>? LoadFailed;

    internal RegistryLocation? SelectedKey => this.RegistryTree.SelectedNode?.Content as RegistryLocation;

    public RegistryTreePane()
    {
        this.InitializeComponent();
        this.RegistryTree.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(this.Tree_DoubleTapped), true);
        PointerEventHandler pointerInput = (_, _) => this.ReleaseViewport();
        this.RegistryTree.AddHandler(PointerPressedEvent, pointerInput, true);
        this.RegistryTree.AddHandler(PointerWheelChangedEvent, pointerInput, true);
        MiddleClick.Attach(
            this.RegistryTree,
            args => MiddleClick.Container<TreeViewItem>(this.RegistryTree, args) is { } item
                ? this.RegistryTree.NodeFromContainer(item)?.Content as RegistryLocation
                : null, location => this.OpenInBackground?.Invoke(location));
    }

    internal void Initialize(BrowserViewModel viewModel, MenuFlyout menu)
    {
        this.ViewModel = viewModel;
        viewModel.Navigated += this.LocationChanged;
        viewModel.TreeRefreshRequested += this.Rebuild;
        viewModel.TreeBranchChanged += this.RefreshBranchAsync;
        viewModel.PropertyChanged += this.EditStateChanged;
        this.RegistryTree.ItemContainerStyle = new Style(typeof(TreeViewItem))
        {
            Setters =
            {
                new Setter(ContextFlyoutProperty, menu),
                new Setter(FocusVisualPrimaryThicknessProperty, new Thickness(1)),
                new Setter(FocusVisualSecondaryThicknessProperty, new Thickness(0))
            }
        };
    }

    internal void FocusTree()
    {
        this.RegistryTree.Focus(FocusState.Keyboard);
    }

    internal async Task NavigateParentAsync()
    {
        if (this.RegistryTree.SelectedNode?.Parent is not { Content: RegistryLocation location } parent)
        {
            return;
        }

        this.ReleaseViewport();
        this.RegistryTree.SelectedNode = parent;
        await this.ViewModel.NavigateAsync(location);
        if (this.RegistryTree.SelectedNode != parent)
        {
            return;
        }

        if (FindDescendant<ListView>(this.RegistryTree) is { } list)
        {
            list.ScrollIntoView(parent);
            list.UpdateLayout();
        }

        if (await this.YieldToUiAsync() && this.RegistryTree.SelectedNode == parent)
        {
            (this.RegistryTree.ContainerFromNode(parent) as Control)?.Focus(FocusState.Keyboard);
        }
    }

    internal RegistryLocation? SelectContextNode(DependencyObject? target)
    {
        if (target is not TreeViewItem item || this.RegistryTree.NodeFromContainer(item) is not
                { Content: RegistryLocation key } node)
        {
            return null;
        }

        this.RegistryTree.SelectedNode = node;
        return key;
    }

    internal void Cancel()
    {
        this.ViewModel.Navigated -= this.LocationChanged;
        this.ViewModel.TreeRefreshRequested -= this.Rebuild;
        this.ViewModel.TreeBranchChanged -= this.RefreshBranchAsync;
        this.ViewModel.PropertyChanged -= this.EditStateChanged;
        this._restoreEditViewport = null;
        this._treeGeneration++;
        this._treeRequest++;
    }

    internal async Task<bool> InitializeNodesAsync()
    {
        this.Rebuild();
        var generation = this._treeGeneration;
        return await this.YieldToUiAsync() && generation == this._treeGeneration;
    }

    internal void SetTracking(bool track)
    {
        if (this._autoSyncTree == track)
        {
            return;
        }

        this._autoSyncTree = track;
        this._treeRequest++;
        if (track)
        {
            this.TrackLocation(this.ViewModel.Location);
        }
    }

    internal void SynchronizeLocation()
    {
        this.LocationChanged(null, EventArgs.Empty);
    }

    private void LocationChanged(object? sender, EventArgs args)
    {
        this._treeRequest++;
        var location = this.ViewModel.Location;
        if (this._treeMachine != location.Machine || this._treeView != location.View)
        {
            this.Rebuild();
        }

        var target = this._autoSyncTree ? location : this._treeRestoreLocation;
        var expand = this._expandInitialKey;
        this._expandInitialKey = false;
        this._treeRestoreLocation = null;
        if (target is not null && (expand || !target.IsSameLocation(this.SelectedKey)))
        {
            this.TrackLocation(target, expand);
        }
    }

    internal void Rebuild()
    {
        this._treeRequest++;
        this._treeGeneration++;
        this._treeRestoreLocation
            = !this._autoSyncTree && this._treeMachine == this.ViewModel.Location.Machine &&
              this._treeView == this.ViewModel.Location.View
                ? this.RegistryTree.SelectedNode?.Content as RegistryLocation ?? this.ViewModel.Location
                : this.ViewModel.Location;
        this._treeMachine = this.ViewModel.Location.Machine;
        this._treeView = this.ViewModel.Location.View;
        this.RegistryTree.RootNodes.Clear();
        var computer = new TreeViewNode { Content = this.ViewModel.Location.GetComputer(), IsExpanded = true };
        foreach (var hive in this.ViewModel.Location.GetHives())
        {
            computer.Children.Add(Node(hive));
        }

        this.RegistryTree.RootNodes.Add(computer);
    }

    public static string TreeGlyph(RegistryLocation location)
    {
        return location.IsComputer ? "\uE7F4" : "\uE8B7";
    }

    private static TreeViewNode Node(RegistryLocation location)
    {
        return new TreeViewNode { Content = location, HasUnrealizedChildren = true };
    }

    private static TreeViewNode? ChildNamed(TreeViewNode node, string name)
    {
        return node.Children.FirstOrDefault(child =>
            child.Content is RegistryLocation key && key.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private TreeViewNode? FindNode(RegistryLocation location)
    {
        if (location.View != this._treeView ||
            !string.Equals(location.Machine, this._treeMachine, StringComparison.OrdinalIgnoreCase) ||
            this.RegistryTree.RootNodes.FirstOrDefault() is not { } computer)
        {
            return null;
        }

        var node = ChildNamed(computer, location.RootName);
        foreach (var segment in location.SubKey.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is null)
            {
                break;
            }

            node = ChildNamed(node, segment);
        }

        return node;
    }

    private async Task RefreshBranchAsync(RegistryLocation parent, (string OldName, string NewName)? rename)
    {
        try
        {
            if (this.FindNode(parent) is { } node && (!node.HasUnrealizedChildren || this._treeLoads.ContainsKey(node)))
            {
                await this.PopulateNodeAsync(node, true, rename);
            }
        }
        catch (Exception ex)
        {
            this.LoadFailed?.Invoke(ex);
        }
    }

    private async Task<TreeViewNode> PopulateNodeAsync(
        TreeViewNode node,
        bool refresh = false,
        (string OldName, string NewName)? rename = null)
    {
        while (this._treeLoads.TryGetValue(node, out var pending))
        {
            node = await pending;
            if (!refresh)
            {
                return node;
            }
        }

        if ((!refresh && !node.HasUnrealizedChildren) || node.Content is not RegistryLocation location)
        {
            return node;
        }

        var generation = this._treeGeneration;
        var load = ReadAsync();
        this._treeLoads.Add(node, load);
        return await load;

        async Task<TreeViewNode> ReadAsync()
        {
            await Task.Yield();
            try
            {
                if (generation != this._treeGeneration || !this.IsAttached(node))
                {
                    return node;
                }

                return refresh
                    ? await this.RefreshChildrenAsync(node, location, rename)
                    : await this.LoadNodeAsync(node, location);
            }
            catch (Exception) when (generation != this._treeGeneration || !this.IsAttached(node))
            {
                return node;
            }
            catch (FileNotFoundException) when (node.Content is RegistryLocation current && current != location)
            {
                return refresh
                    ? await this.RefreshChildrenAsync(node, current, rename)
                    : await this.LoadNodeAsync(node, current);
            }
            finally
            {
                this._treeLoads.Remove(node);
            }
        }
    }

    private async Task<TreeViewNode> RefreshChildrenAsync(
        TreeViewNode node,
        RegistryLocation location,
        (string OldName, string NewName)? rename)
    {
        var generation = this._treeGeneration;
        var names = await this.ViewModel.ReadSubKeysAsync(location);
        if (generation == this._treeGeneration && this.FindNode(location) == node)
        {
            this.UpdateChildren(node, location, names, rename);
        }

        return node;
    }

    private void UpdateChildren(
        TreeViewNode node,
        RegistryLocation location,
        string[] names,
        (string OldName, string NewName)? rename)
    {
        var selected = this.RegistryTree.SelectedNode;
        var children = node.Children;
        var comparer = StringComparer.OrdinalIgnoreCase;
        TreeViewNode? moved = null;
        if (rename is { } change && !comparer.Equals(change.OldName, change.NewName) &&
            Array.BinarySearch(names, change.NewName, comparer) >= 0 &&
            Array.BinarySearch(names, change.OldName, comparer) < 0 && ChildNamed(node, change.OldName) is { } original)
        {
            children.Remove(original);
            Rebase(original, location.Child(change.NewName));
            moved = original;
        }

        var index = 0;
        foreach (var name in names)
        {
            while (index < children.Count &&
                   comparer.Compare(((RegistryLocation)children[index].Content).Name, name) < 0)
            {
                children.RemoveAt(index);
            }

            if (index == children.Count || !comparer.Equals(((RegistryLocation)children[index].Content).Name, name))
            {
                var child = moved?.Content is RegistryLocation key && comparer.Equals(key.Name, name)
                    ? moved
                    : Node(location.Child(name));
                children.Insert(index, child);
            }
            else if (((RegistryLocation)children[index].Content).Name != name)
            {
                Rebase(children[index], location.Child(name));
            }

            index++;
        }

        while (children.Count > index)
        {
            children.RemoveAt(children.Count - 1);
        }

        if (selected is not null)
        {
            var keep = this.IsAttached(selected) ? selected : node;
            if (this.RegistryTree.SelectedNode != keep)
            {
                this.RegistryTree.SelectedNode = keep;
            }
        }
    }

    private void EditStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(BrowserViewModel.CanPrompt))
        {
            return;
        }

        if (this.ViewModel.CanPrompt)
        {
            var restore = this._restoreEditViewport;
            this._restoreEditViewport = null;
            restore?.Invoke();
        }
        else
        {
            this._restoreEditViewport = this.CaptureViewport();
        }
    }

    private Action? CaptureViewport()
    {
        if (!this.IsLoaded || FindDescendant<ListView>(this.RegistryTree) is not { } list ||
            FindDescendant<ScrollViewer>(list) is not { ViewportHeight: > 0 } scroll)
        {
            return null;
        }

        var horizontal = scroll.HorizontalOffset;
        var vertical = scroll.VerticalOffset;
        object? anchor = null;
        var firstTop = scroll.ViewportHeight;
        // Native AOT can expose a generic Panel, including a partially clipped first row.
        if (list.ItemsPanelRoot is { } panel)
        {
            foreach (var child in panel.Children)
            {
                var y = child.TransformToVisual(scroll).TransformPoint(default).Y;
                if (y < firstTop && y + child.RenderSize.Height > 0 &&
                    list.ItemFromContainer(child) is TreeViewNode node)
                {
                    anchor = node;
                    firstTop = y;
                }
            }
        }

        double? AnchorTop()
        {
            var row = list.ItemsPanelRoot?.Children.FirstOrDefault(child => ReferenceEquals(
                list.ItemFromContainer(child), anchor));
            return anchor is not null && row is not null
                ? row.TransformToVisual(scroll).TransformPoint(default).Y
                : null;
        }

        var top = AnchorTop();
        var generation = this._treeGeneration;
        var version = this._viewportVersion;
        var originalSelection = this.RegistryTree.SelectedNode;
        return () =>
        {
            if (generation != this._treeGeneration || version != this._viewportVersion || !scroll.IsLoaded)
            {
                return;
            }

            if (anchor is TreeViewNode old && !this.IsAttached(old) && originalSelection is not null &&
                !this.IsAttached(originalSelection) && this.RegistryTree.SelectedNode is { } survivor)
            {
                list.ScrollIntoView(survivor);
                return;
            }

            scroll.UpdateLayout();
            Correct();
            // Dialog focus restoration and tree selection can both scroll on the next frame.
            CompositionTarget.Rendered += Restore;
            scroll.ViewChanged += ViewChanged;
            this.Unloaded += UnloadedBeforeRender;
        };

        void Stop()
        {
            CompositionTarget.Rendered -= Restore;
            scroll.ViewChanged -= ViewChanged;
            this.Unloaded -= UnloadedBeforeRender;
        }

        void UnloadedBeforeRender(object sender, RoutedEventArgs args)
        {
            Stop();
        }

        void Restore(object? sender, object args)
        {
            CompositionTarget.Rendered -= Restore;
            if (!this.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        Correct();
                        Stop();
                    }))
            {
                Stop();
            }
        }

        void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
        {
            if (!args.IsIntermediate)
            {
                Correct();
            }
        }

        void Correct()
        {
            if (generation != this._treeGeneration || version != this._viewportVersion || !scroll.IsLoaded)
            {
                return;
            }

            var target = top.HasValue && AnchorTop() is { } current
                ? scroll.VerticalOffset + current - top.Value
                : vertical;
            if (Math.Abs(target - scroll.VerticalOffset) > 0.5 || Math.Abs(horizontal - scroll.HorizontalOffset) > 0.5)
            {
                scroll.ViewChanged -= ViewChanged;
                scroll.ChangeView(horizontal, target, null, true);
            }
        }
    }

    private void ReleaseViewport()
    {
        this._viewportVersion++;
        this._restoreEditViewport = null;
    }

    private bool IsAttached(TreeViewNode node)
    {
        var root = this.RegistryTree.RootNodes.FirstOrDefault();
        for (TreeViewNode? ancestor = node; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor == root)
            {
                return true;
            }
        }

        return false;
    }

    private static void Rebase(TreeViewNode node, RegistryLocation location)
    {
        node.Content = location;
        foreach (var child in node.Children)
        {
            Rebase(child, location.Child(((RegistryLocation)child.Content).Name));
        }
    }

    private async Task<TreeViewNode> LoadNodeAsync(TreeViewNode node, RegistryLocation location)
    {
        var generation = this._treeGeneration;
        var names = await this.ViewModel.ReadSubKeysAsync(location);
        if (generation != this._treeGeneration)
        {
            return node;
        }

        // Detached nodes avoid a native tree scan for every child, even when the parent is collapsed.
        var loaded = new TreeViewNode { Content = location };
        var slice = Stopwatch.StartNew();
        foreach (var name in names)
        {
            loaded.Children.Add(Node(location.Child(name)));
            if (slice.ElapsedMilliseconds < 8)
            {
                continue;
            }

            if (!await this.YieldToUiAsync() || generation != this._treeGeneration)
            {
                return node;
            }

            slice.Restart();
        }

        if (!this.IsAttached(node) || node.Parent is not { } parent)
        {
            return node;
        }

        var index = parent.Children.IndexOf(node);
        if (index < 0)
        {
            return node;
        }

        var selected = this.RegistryTree.SelectedNode == node;
        if (node.Content is RegistryLocation current && current != location)
        {
            Rebase(loaded, current);
        }

        loaded.IsExpanded = node.IsExpanded;
        parent.Children[index] = loaded;
        if (selected)
        {
            this.RegistryTree.SelectedNode = loaded;
        }

        return loaded;
    }

    private Task<bool> YieldToUiAsync()
    {
        var completion = new TaskCompletionSource<bool>();
        if (!this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => completion.SetResult(true)))
        {
            completion.SetResult(false);
        }

        return completion.Task;
    }

    internal async void TrackLocation(RegistryLocation location, bool expand = false)
    {
        try
        {
            await this.SyncTreeAsync(location, expand);
        }
        catch (Exception ex)
        {
            this.LoadFailed?.Invoke(ex);
        }
    }

    private async Task SyncTreeAsync(RegistryLocation location, bool expand)
    {
        this.ReleaseViewport();
        var request = ++this._treeRequest;
        var computer = this.RegistryTree.RootNodes.FirstOrDefault();
        if (computer is null)
        {
            return;
        }

        computer.IsExpanded = true;
        var node = location.IsComputer
            ? computer
            : computer.Children.FirstOrDefault(n => n.Content is RegistryLocation root && root.Hive == location.Hive);
        if (node is null)
        {
            return;
        }

        foreach (var segment in location.SubKey.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = await this.PopulateNodeAsync(node);
            if (request != this._treeRequest)
            {
                return;
            }

            var child = ChildNamed(node, segment);
            if (child is null)
            {
                // The current key may have been created after this branch was expanded.
                node = await this.PopulateNodeAsync(node, true);
                if (request != this._treeRequest)
                {
                    return;
                }

                child = ChildNamed(node, segment);
            }

            node.IsExpanded = true;
            if (child is null)
            {
                break;
            }

            node = child;
        }

        this.RegistryTree.SelectedNode = node;
        if (expand && !location.IsComputer && location.IsSameLocation(node.Content as RegistryLocation))
        {
            node.IsExpanded = true;
            node = await this.PopulateNodeAsync(node);
            if (request != this._treeRequest)
            {
                return;
            }
        }

        this.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // TreeView does not realize containers for distant nodes until its native list scrolls.
                if (request == this._treeRequest)
                {
                    FindDescendant<ListView>(this.RegistryTree)?.ScrollIntoView(node);
                }
            });
    }

    private static T? FindDescendant<T>(DependencyObject element)
        where T : DependencyObject
    {
        if (element is T match)
        {
            return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            if (FindDescendant<T>(VisualTreeHelper.GetChild(element, index)) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private async void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        try
        {
            await this.PopulateNodeAsync(args.Node);
        }
        catch (Exception ex)
        {
            this.LoadFailed?.Invoke(ex);
        }
    }

    private async void Tree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: RegistryLocation location })
        {
            await this.ViewModel.NavigateAsync(location);
        }
    }

    private void Tree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        var element = args.OriginalSource as DependencyObject;
        while (element is not null && element is not TreeViewItem)
        {
            // The native chevron already handles its own expansion gesture.
            if (element is FrameworkElement { Name: "ExpandCollapseChevron" })
            {
                return;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        if (element is not TreeViewItem item || this.RegistryTree.NodeFromContainer(item) is not { } node)
        {
            return;
        }

        args.Handled = true;
        if (node.HasUnrealizedChildren || node.Children.Count > 0)
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    private void Tree_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        this.ReleaseViewport();
        if (RegistryKeyboardShortcuts.IsPlainControl(this.XamlRoot) && args.Key is VirtualKey.C or VirtualKey.V)
        {
            args.Handled = true;
            ICommand command = args.Key == VirtualKey.C
                ? this.ViewModel.CopyTreeKeyCommand
                : this.ViewModel.PasteIntoCommand;
            if (command.CanExecute(this.SelectedKey))
            {
                command.Execute(this.SelectedKey);
            }

            return;
        }

        if (args.Key is VirtualKey.F2 or VirtualKey.Delete && this.SelectedKey is { } key)
        {
            args.Handled = true;
            var command = args.Key == VirtualKey.F2
                ? this.ViewModel.RenameTreeKeyCommand
                : this.ViewModel.DeleteTreeKeyCommand;
            if (command.CanExecute(key))
            {
                command.Execute(key);
            }
        }
    }
}
