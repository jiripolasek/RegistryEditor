// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Controls.Shared;

public sealed partial class ToolToolbar : UserControl
{
    public event TextChangedEventHandler? SearchChanged;

    public event EventHandler? PresentationChanged;

    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText), typeof(string), typeof(ToolToolbar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SearchPlaceholderProperty = DependencyProperty.Register(
        nameof(SearchPlaceholder), typeof(string), typeof(ToolToolbar), new PropertyMetadata("Search items…"));

    public static readonly DependencyProperty SearchNameProperty = DependencyProperty.Register(
        nameof(SearchName), typeof(string), typeof(ToolToolbar), new PropertyMetadata("Search tool items"));

    public static readonly DependencyProperty FiltersContentProperty = DependencyProperty.Register(
        nameof(FiltersContent), typeof(UIElement), typeof(ToolToolbar), new PropertyMetadata(null));

    public static readonly DependencyProperty CommandsContentProperty = DependencyProperty.Register(
        nameof(CommandsContent), typeof(UIElement), typeof(ToolToolbar), new PropertyMetadata(
            null, (owner, args) =>
            {
                if (args.NewValue is ListCommandBar bar)
                {
                    bar.UseToolToolbarLayout();
                    bar.TrailingContent = ((ToolToolbar)owner).Utilities;
                }
            }));

    public static readonly DependencyProperty ShowColumnsProperty = DependencyProperty.Register(
        nameof(ShowColumns), typeof(bool), typeof(ToolToolbar),
        new PropertyMetadata(true, (owner, _) => ((ToolToolbar)owner).UpdatePresentation()));

    private readonly FontIconSource _viewIcon = new()
    {
        FontFamily = new FontFamily("/Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"), Glyph = "\U000F0764"
    };

    private bool _searchOnly;
    private ToolCommand? _preview;

    public string SearchText
    {
        get => (string)this.GetValue(SearchTextProperty);
        set => this.SetValue(SearchTextProperty, value);
    }

    public string SearchPlaceholder
    {
        get => (string)this.GetValue(SearchPlaceholderProperty);
        set => this.SetValue(SearchPlaceholderProperty, value);
    }

    public string SearchName
    {
        get => (string)this.GetValue(SearchNameProperty);
        set => this.SetValue(SearchNameProperty, value);
    }

    public UIElement? FiltersContent
    {
        get => (UIElement?)this.GetValue(FiltersContentProperty);
        set => this.SetValue(FiltersContentProperty, value);
    }

    public UIElement? CommandsContent
    {
        get => (UIElement?)this.GetValue(CommandsContentProperty);
        set => this.SetValue(CommandsContentProperty, value);
    }

    public bool ShowColumns
    {
        get => (bool)this.GetValue(ShowColumnsProperty);
        set => this.SetValue(ShowColumnsProperty, value);
    }

    internal TextBox SearchBox => this.Input;

    internal ToolCommand ViewCommand { get; }

    public ToolToolbar()
    {
        this.InitializeComponent();
        this.UtilitiesHost.Content = null;
        this.ViewCommand
            = new ToolCommand("List view", Symbol.View, ToolCommandPlacement.Overflow)
            {
                Flyout = this.ViewMenu, IconSource = this._viewIcon
            };
    }

    public Visibility FilterVisibility(UIElement? content)
    {
        return content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void ShowSearchOnly()
    {
        this._searchOnly = true;
        if (this.CommandsContent is ListCommandBar bar)
        {
            bar.TrailingContent = null;
        }

        this.UtilitiesHost.Content = this.Utilities;
        this.Filters.Visibility = this.Commands.Visibility
            = this.ViewButton.Visibility = this.PreviewButton.Visibility = Visibility.Collapsed;
        Grid.SetColumn(this.UtilitiesHost, 0);
        Grid.SetColumnSpan(this.UtilitiesHost, 3);
        this.Utilities.HorizontalAlignment = HorizontalAlignment.Stretch;
        this.SearchPanel.Width = double.NaN;
    }

    internal void SetPreviewCommand(ToolCommand command)
    {
        if (this._preview is not null)
        {
            this._preview.PropertyChanged -= this.Preview_Changed;
        }

        this._preview = command;
        command.PropertyChanged += this.Preview_Changed;
        this.UpdatePreview();
    }

    private void Preview_Changed(object? sender, PropertyChangedEventArgs args)
    {
        this.UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (this._preview is not { } command)
        {
            return;
        }

        this.PreviewButton.Command = command.Command;
        this.PreviewButton.IsEnabled = command.IsEnabled;
        this.PreviewButton.Visibility = command.IsVisible && !this._searchOnly ? Visibility.Visible : Visibility.Collapsed;
        this.PreviewIcon.Glyph = command.IsChecked ? "\uE89F" : "\uE8A0";
        AutomationProperties.SetName(this.PreviewButton, command.IsChecked ? "Hide details" : "Show details");
        AutomationProperties.SetItemStatus(this.PreviewButton, command.IsChecked ? "Preview open" : "Preview closed");
        ToolTipService.SetToolTip(this.PreviewButton, (command.IsChecked ? "Hide details" : "Show details") + " (Alt+P)");
    }

    private void Preview_Click(object sender, RoutedEventArgs args)
    {
        if (this._preview is { } command)
        {
            command.IsChecked = !command.IsChecked;
        }
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs args)
    {
        this.SearchChanged?.Invoke(this, args);
    }

    private void Columns_Click(object sender, RoutedEventArgs args)
    {
        this.ShowColumns = true;
        this.ColumnsChoice.IsChecked = true;
    }

    private void Rows_Click(object sender, RoutedEventArgs args)
    {
        this.ShowColumns = false;
        this.RowsChoice.IsChecked = true;
    }

    private void UpdatePresentation()
    {
        if (this.ColumnsChoice is null)
        {
            return;
        }

        this.ColumnsChoice.IsChecked = this.ShowColumns;
        this.RowsChoice.IsChecked = !this.ShowColumns;
        this.ViewIcon.Glyph = this._viewIcon.Glyph = this.ShowColumns ? "\U000F0764" : "\uE03E";
        var name = this.ShowColumns ? "Columns" : "Stacked rows";
        AutomationProperties.SetName(this.ViewButton, "List presentation: " + name);
        ToolTipService.SetToolTip(this.ViewButton, name);
        this.PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.SearchPanel.Width = this._searchOnly ? Math.Max(0, args.NewSize.Width - 10) : args.NewSize.Width < 900 ? 160 : 240;
    }
}
