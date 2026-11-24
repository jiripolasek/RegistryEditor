// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Text.Json;
using JPSoftworks.Regedit.Controls.Lists;
using JPSoftworks.Regedit.Controls.Shared;
using JPSoftworks.Regedit.Infrastructure.Collections;
using JPSoftworks.Regedit.Models.Lists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace JPSoftworks.ColumnList.Sample;

public sealed partial class MainPage : Page
{
    internal const int EntryCount = 10_000;

    private static readonly Dictionary<string, Func<SampleEntry, IComparable>> SortKeys = new()
    {
        ["Name"] = entry => entry.Name,
        ["Type"] = entry => entry.Kind,
        ["Data"] = entry => entry.Data,
        ["LastWrite"] = entry => entry.LastWrite,
        ["Size"] = entry => entry.Size
    };

    private readonly string _layoutFile = Path.Combine(Path.GetTempPath(), "ColumnList.Sample.layout.json");
    private SampleEntry[] _source = SampleData.Entries(EntryCount);

    internal RangeObservableCollection<object> RegistrySelection { get; } = [];

    internal RangeObservableCollection<object> ToolSelection { get; } = [];

    internal int LayoutChanges { get; private set; }

    internal string? LastSortRequest { get; private set; }

    internal string? LastRename { get; private set; }

    internal IReadOnlyList<SampleEntry> Entries => (IReadOnlyList<SampleEntry>)this.Registry.ItemsSource!;

    internal string LayoutFile => this._layoutFile;

    public MainPage()
    {
        this.InitializeComponent();
        this.Registry.FindColumn("Name")!.EditText = item => (item as SampleEntry)?.Name;
        this.Registry.FindColumn("Type")!.CellText = item => (item as SampleEntry)?.Kind;
        this.Registry.FindColumn("LastWrite")!.CellText = item => (item as SampleEntry)?.LastWriteText;
        this.Registry.FindColumn("Size")!.CellText = item => (item as SampleEntry)?.SizeText;
        this.Tools.FindColumn("Subject")!.CellText = item => (item as SampleEvent)?.Subject;
        this.Tools.FindColumn("Path")!.CellText = item => (item as SampleEvent)?.Path;
        this.Tools.FindColumn("Summary")!.CellText = item => (item as SampleEvent)?.Summary;

        this.Registry.ItemsSource = new ObservableCollection<SampleEntry>(this._source);
        this.Registry.SelectedItems = this.RegistrySelection;
        this.Tools.ItemsSource = SampleData.Events(400);
        this.Tools.SelectedItems = this.ToolSelection;
        this.RegistrySelection.CollectionChanged += (_, _) => this.UpdateInspector();
        this.ToolSelection.CollectionChanged += (_, _) => this.UpdateInspector();
        ListContextMenu.Attach(this.Registry.ListView, this.RowMenu);
        this.Loaded += (_, _) => this.UpdateInspector();
    }

    private void EntryName_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        var item = ((FrameworkElement)sender).DataContext;
        var entries = this.Registry.PrepareItemDrag(item).Cast<SampleEntry>();
        args.AllowedOperations = args.Data.RequestedOperation = DataPackageOperation.Copy;
        args.Data.SetText(string.Join(Environment.NewLine, entries.Select(entry => entry.Name)));
    }

    private void Names_DragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = args.DataView.Contains(StandardDataFormats.Text)
            ? args.AllowedOperations & DataPackageOperation.Copy
            : DataPackageOperation.None;
        args.Handled = true;
    }

    private async void Names_Drop(object sender, DragEventArgs args)
    {
        this.Names_DragOver(sender, args);
        if (args.AcceptedOperation == DataPackageOperation.None)
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            this.DroppedNames.Text = await args.DataView.GetTextAsync();
        }
        catch (Exception)
        {
            args.AcceptedOperation = DataPackageOperation.None;
            this.DroppedNames.Text = "Couldn't read the dropped text.";
        }
        finally
        {
            deferral.Complete();
        }
    }

    internal void SaveLayout()
    {
        File.WriteAllText(
            this._layoutFile, JsonSerializer.Serialize(this.Registry.EffectiveLayout, ColumnListJsonContext.Default.ColumnLayout));
    }

    internal void ReloadLayout()
    {
        this.Registry.Layout = File.Exists(this._layoutFile)
            ? JsonSerializer.Deserialize(File.ReadAllText(this._layoutFile), ColumnListJsonContext.Default.ColumnLayout)
            : null;
        this.UpdateInspector();
    }

    private void Registry_SortRequested(object? sender, string id)
    {
        this.LastSortRequest = id;
        var descending = this.Registry.SortColumnId == id && !this.Registry.SortDescending;
        var key = SortKeys[id];
        this._source = descending ? this._source.OrderByDescending(key).ToArray() : this._source.OrderBy(key).ToArray();

        // Replacing the source clears the list's selection; restore it from the host-owned copy afterwards.
        var selected = this.RegistrySelection.ToArray();
        this.Registry.ItemsSource = new ObservableCollection<SampleEntry>(this._source);
        this.RegistrySelection.ReplaceAll(selected);

        this.Registry.SortColumnId = id;
        this.Registry.SortDescending = descending;
        this.UpdateInspector();
    }

    private void Registry_EditCommitting(object? sender, ColumnEditCommittingEventArgs args)
    {
        var name = args.NewText.Trim();
        if (name.Length == 0)
        {
            args.Cancel = true;
            args.Error = "A name cannot be empty.";
            return;
        }

        if (this.Entries.Any(entry =>
                !ReferenceEquals(entry, args.Item) && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            args.Cancel = true;
            args.Error = $"A value named \"{name}\" already exists.";
            return;
        }

        ((SampleEntry)args.Item).Name = name;
        this.LastRename = name;
    }

    private void Tools_EditCommitting(object? sender, ColumnEditCommittingEventArgs args)
    {
        ((SampleEvent)args.Item).Summary = args.NewText;
    }

    private void Registry_LayoutChanged(object? sender, ColumnLayout layout)
    {
        this.LayoutChanges++;
        this.UpdateInspector();
    }

    private void Tools_LayoutChanged(object? sender, ColumnLayout layout)
    {
        this.UpdateInspector();
    }

    private void DockedSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        // IsOn is set while the page is still loading, before the Tools field is assigned.
        if (this.Tools is not null)
        {
            this.Tools.ShowColumns = this.DockedSwitch.IsOn;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs args)
    {
        this.Registry.ResetLayout();
        this.Tools.ResetLayout();
    }

    private void SelectionCheckBoxes_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (this.Registry is not null && ((ComboBox)sender).SelectedIndex is var index and >= 0)
        {
            this.Registry.SelectionCheckBoxes = (SelectionCheckBoxMode)index;
        }
    }

    private void DragSelectionOption_Click(object sender, RoutedEventArgs args)
    {
        this.Registry.IsDragSelectionEnabled = this.DragSelectionOption.IsChecked == true;
    }

    private void FitAll_Click(object sender, RoutedEventArgs args)
    {
        this.Registry.AutoFitAllColumns();
    }

    private void Save_Click(object sender, RoutedEventArgs args)
    {
        this.SaveLayout();
    }

    private void Reload_Click(object sender, RoutedEventArgs args)
    {
        this.ReloadLayout();
    }

    private void Density_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (this.Registry is null)
        {
            return;
        }

        var spacing = this.DensityBox.SelectedIndex switch { 0 => 0d, 2 => 10d, _ => 3d };
        ListDensity.SetSpacing(this.Registry.ListView, spacing);
        ListDensity.SetSpacing(this.Tools.ListView, spacing);
    }

    private MenuFlyout RowMenu()
    {
        var menu = new MenuFlyout();
        var count = this.Registry.ListView.SelectedItems.Count;
        ListContextMenu.Add(menu, $"Copy {count} name(s)", () => { }, count > 0);
        ListContextMenu.Add(menu, "Select all", () => this.Registry.ListView.SelectAll(), true);
        return menu;
    }

    private void UpdateInspector()
    {
        this.SelectionText.Text = $"Selected: {this.RegistrySelection.Count} registry rows, {this.ToolSelection.Count} tool rows";
        this.SortText.Text = this.Registry.SortColumnId is { } sort
            ? $"Sorted by {sort} {(this.Registry.SortDescending ? "descending" : "ascending")}"
            : "Unsorted";
        this.LayoutText.Text = JsonSerializer.Serialize(
            this.Registry.EffectiveLayout, new JsonSerializerOptions(ColumnListJsonContext.Default.Options) { WriteIndented = true });
    }
}
