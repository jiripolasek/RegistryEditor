// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace JPSoftworks.Regedit.Controls.Shared;

public sealed partial class ListCommandBar : CommandBar
{
    public static readonly DependencyProperty TrailingContentProperty = DependencyProperty.Register(
        nameof(TrailingContent), typeof(UIElement), typeof(ListCommandBar),
        new PropertyMetadata(null, (owner, _) => ((ListCommandBar)owner).UpdateTrailingContent()));

    private readonly List<(ToolCommand Command, ButtonBase Button, PropertyChangedEventHandler Handler)> _generated = [];

    private bool _toolToolbar;

    private ContentPresenter? _trailing;

    public UIElement? TrailingContent
    {
        get => (UIElement?)this.GetValue(TrailingContentProperty);
        set => this.SetValue(TrailingContentProperty, value);
    }

    public ListCommandBar()
    {
        // Derived command bars otherwise pick the legacy template and its darker Reveal button styles.
        this.Style = (Style)Application.Current.Resources["DefaultCommandBarStyle"];
        this.Resources["AppBarThemeCompactHeight"] = 36d;
        this.Resources["AppBarThemeMinHeight"] = 36d;
        this.Resources["AppBarButtonInnerBorderMargin"] = new Thickness(2, 0, 2, 0);
        this.Resources["AppBarButtonContentViewboxMargin"] = new Thickness(12, 10, 0, 10);
        this.Resources["AppBarExpandButtonThemeWidth"] = 40d;
        this.PrimaryCommands.VectorChanged += (_, _) => this.UpdateCommands();
        this.SecondaryCommands.VectorChanged += (_, _) => this.UpdateCommands();
        this.RegisterPropertyChangedCallback(DefaultLabelPositionProperty, (_, _) => this.UpdateCommands());
        this.RegisterPropertyChangedCallback(IsOpenProperty, (_, _) => this.PositionOverflow());
        this.Loaded += (_, _) => this.UpdateCommands();
        this.Unloaded += (_, _) => this.IsOpen = false;
    }

    internal void UseToolToolbarLayout()
    {
        this._toolToolbar = true;
        this.Resources["AppBarThemeCompactHeight"] = 32d;
        this.Resources["AppBarThemeMinHeight"] = 32d;
        this.Resources["AppBarButtonInnerBorderMargin"] = new Thickness(2, 0, 2, 0);
        this.Resources["AppBarButtonContentViewboxMargin"] = new Thickness(8, 6, 0, 6);
    }

    /// <summary>
    /// Replaces the generated commands with buttons for <paramref name="commands"/>. Declared XAML commands are
    /// kept. Buttons follow later changes to each <see cref="ToolCommand"/>.
    /// </summary>
    internal void SetCommands(IEnumerable<ToolCommand>? commands)
    {
        foreach (var (command, button, handler) in this._generated)
        {
            command.PropertyChanged -= handler;
            var element = (ICommandBarElement)button;
            this.PrimaryCommands.Remove(element);
            this.SecondaryCommands.Remove(element);
        }

        this._generated.Clear();
        foreach (var command in commands ?? [])
        {
            var button = CreateButton(command);
            PropertyChangedEventHandler handler = (_, _) =>
            {
                Apply(command, button);
                if (this._toolToolbar && button.IsLoaded)
                {
                    this.AlignCommand(button);
                }
            };
            command.PropertyChanged += handler;
            this._generated.Add((command, button, handler));
            var target = command.Placement == ToolCommandPlacement.Primary ? this.PrimaryCommands : this.SecondaryCommands;
            target.Add((ICommandBarElement)button);
        }
    }

    private static ButtonBase CreateButton(ToolCommand command)
    {
        var iconSource = command.IconSource ?? new FontIconSource { Glyph = SymbolGlyphs.Get(command.Symbol) };
        ButtonBase button;
        if (command.IsToggle)
        {
            // Toggles keep native icon colors so the checked (accent background) state stays readable.
            var toggle = new AppBarToggleButton { Icon = new IconSourceElement { IconSource = iconSource } };
            toggle.Click += (_, _) => command.IsChecked = toggle.IsChecked == true;
            button = toggle;
        }
        else
        {
            button = new AppBarButton { Icon = new CommandIcon { IconSource = iconSource }, Flyout = command.Flyout };
        }

        if (command.AutomationId is { } id)
        {
            AutomationProperties.SetAutomationId(button, id);
        }

        if (command.AutomationName is { } name)
        {
            AutomationProperties.SetName(button, name);
        }

        command.Attach?.Invoke(button);
        Apply(command, button);
        return button;
    }

    private static void Apply(ToolCommand command, ButtonBase button)
    {
        if (command.IconSource is null)
        {
            var icon = button is AppBarButton action ? action.Icon : ((AppBarToggleButton)button).Icon;
            if (icon is CommandIcon commandIcon)
            {
                commandIcon.Symbol = command.Symbol;
            }
            else if (icon is IconSourceElement source && (source.IconSource as FontIconSource)?.Glyph != SymbolGlyphs.Get(command.Symbol))
            {
                source.IconSource = new FontIconSource { Glyph = SymbolGlyphs.Get(command.Symbol) };
            }
        }

        if (button is AppBarToggleButton toggle)
        {
            toggle.Label = command.Label;
            toggle.KeyboardAcceleratorTextOverride = command.ShortcutText ?? string.Empty;
            toggle.IsChecked = command.IsChecked;
        }
        else
        {
            ((AppBarButton)button).Label = command.Label;
            ((AppBarButton)button).KeyboardAcceleratorTextOverride = command.ShortcutText ?? string.Empty;
        }

        if (!ReferenceEquals(button.Command, command.Command))
        {
            button.Command = command.Command;
        }

        button.CommandParameter = command.CommandParameter;
        if (command.Command is null)
        {
            button.IsEnabled = command.IsEnabled;
        }

        ToolTipService.SetToolTip(button, command.ToolTip);
        AutomationProperties.SetHelpText(button, command.ToolTip ?? string.Empty);
        button.Visibility = command.IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCommands()
    {
        // Icon-only bars: WinUI's default style keeps a 68 px labeled width, so size each primary button like
        // the overflow button (36 px wide inside the 2 px inner-border margins).
        var compact = this.DefaultLabelPosition == CommandBarDefaultLabelPosition.Collapsed;
        var labelPosition = compact ? CommandBarLabelPosition.Collapsed : CommandBarLabelPosition.Default;
        foreach (var command in this.PrimaryCommands)
        {
            switch (command)
            {
                case AppBarButton button:
                    button.LabelPosition = labelPosition;
                    break;
                case AppBarToggleButton toggle:
                    toggle.LabelPosition = labelPosition;
                    break;
                default:
                    continue;
            }

            var element = (FrameworkElement)command;
            if (compact)
            {
                element.Width = 40;
            }
            else
            {
                element.ClearValue(WidthProperty);
            }
        }

        foreach (var button in this.PrimaryCommands.Concat(this.SecondaryCommands).OfType<AppBarButton>())
        {
            button.CornerRadius = new CornerRadius(8);
            button.Loaded -= this.Command_Loaded;
            button.Loaded += this.Command_Loaded;
            this.UpdateIcon(button);
        }

        foreach (var toggle in this.PrimaryCommands.Concat(this.SecondaryCommands).OfType<AppBarToggleButton>())
        {
            toggle.Loaded -= this.Command_Loaded;
            toggle.Loaded += this.Command_Loaded;
        }
    }

    private void Command_Loaded(object sender, RoutedEventArgs args)
    {
        this.AlignCommand((ButtonBase)sender);
    }

    private void AlignCommand(ButtonBase button)
    {
        if (button is AppBarButton action)
        {
            this.UpdateIcon(action);
        }

        button.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(button) == 0 || VisualTreeHelper.GetChild(button, 0) is not FrameworkElement root)
        {
            return;
        }

        var labelState = VisualStateManager.GetVisualStateGroups(root).SelectMany(group => group.States)
            .FirstOrDefault(state => state.Name == "LabelOnRight");
        var labelMargin = labelState?.Storyboard?.Children.OfType<ObjectAnimationUsingKeyFrames>().FirstOrDefault(animation =>
            Storyboard.GetTargetName(animation) == "TextLabel" && Storyboard.GetTargetProperty(animation) == "Margin");
        if (labelMargin is null || labelMargin.KeyFrames.Count == 0)
        {
            return;
        }

        // shortcut: WinUI's label margin is static; recheck this native state when upgrading templates.
        var labelOnRight = this.DefaultLabelPosition == CommandBarDefaultLabelPosition.Right
                           && button switch
                           {
                               AppBarButton actionButton => !actionButton.IsInOverflow
                                                            && actionButton.LabelPosition != CommandBarLabelPosition.Collapsed,
                               AppBarToggleButton toggle => !toggle.IsInOverflow
                                                            && toggle.LabelPosition != CommandBarLabelPosition.Collapsed,
                               _ => false
                           };
        if (labelOnRight)
        {
            VisualStateManager.GoToState(button, "FullSize", false);
        }

        labelMargin.KeyFrames[0].Value = this._toolToolbar ? new Thickness(8, 0, 8, 0) : new Thickness(8, 10, 12, 10);
        var chevronMargin = labelState?.Storyboard?.Children.OfType<ObjectAnimationUsingKeyFrames>().FirstOrDefault(animation =>
            Storyboard.GetTargetName(animation) == "SubItemChevron" && Storyboard.GetTargetProperty(animation) == "Margin");
        if (chevronMargin is { KeyFrames.Count: > 0 } && root.FindName("SubItemChevron") is FrameworkElement chevron)
        {
            chevronMargin.KeyFrames[0].Value = new Thickness(-7, 0, 12, 0);
            chevron.VerticalAlignment = VerticalAlignment.Center;
        }

        if (this._toolToolbar)
        {
            if (root.FindName("TextLabel") is FrameworkElement label)
            {
                label.VerticalAlignment = VerticalAlignment.Center;
            }

            if (root.FindName("ContentViewbox") is FrameworkElement icon)
            {
                icon.VerticalAlignment = VerticalAlignment.Center;
            }
        }

        if (labelOnRight)
        {
            VisualStateManager.GoToState(button, "LabelOnRight", false);
        }
    }

    private void UpdateIcon(AppBarButton button)
    {
        if (button.Icon is not { } icon)
        {
            return;
        }

        // Icon colors are owned by CommandIcon; the bar only applies its compact layout.
        var compact = this.DefaultLabelPosition == CommandBarDefaultLabelPosition.Collapsed;
        icon.Width = icon.Height = double.NaN;
        icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        icon.RenderTransform = compact && icon is SymbolIcon or CommandIcon ? new ScaleTransform { ScaleX = 0.8, ScaleY = 0.8 } : null;
        if (icon is CommandIcon commandIcon)
        {
            commandIcon.SetHost(button);
            commandIcon.IsCompact = compact;
        }
    }

    protected override void OnApplyTemplate()
    {
        if (this._trailing is not null)
        {
            this._trailing.Content = null;
            this._trailing = null;
        }

        base.OnApplyTemplate();
        this.UpdateTrailingContent();
        // shortcut: uses the native PrimaryItemsControl part; recheck it when upgrading WinUI templates.
        if (this.GetTemplateChild("PrimaryItemsControl") is FrameworkElement commands)
        {
            commands.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        if (this.GetTemplateChild("MoreButton") is Button more)
        {
            // Use our toolbar style so surrounding controls cannot supply a different ellipsis style.
            more.Style = (Style)Application.Current.Resources["ToolbarButtonStyle"];
            more.Width = 36;
            more.Height = this._toolToolbar || double.IsNaN(this.Height) ? 32 : Math.Max(20, this.Height - 8);
            more.MinHeight = 0;
            more.Margin = this._toolToolbar ? new Thickness(2, 0, 2, 0) : new Thickness(2, 4, 2, 4);
            if (this._toolToolbar)
            {
                more.VerticalAlignment = VerticalAlignment.Center;
            }

            more.Padding = new Thickness(0);
            more.HorizontalContentAlignment = HorizontalAlignment.Center;
            more.VerticalContentAlignment = VerticalAlignment.Center;
        }

        if (this.GetTemplateChild("EllipsisIcon") is FontIcon ellipsis)
        {
            ellipsis.FontSize = 16;
            ellipsis.Width = ellipsis.Height = 20;
            ellipsis.IsTextScaleFactorEnabled = false;
        }
    }

    private void UpdateTrailingContent()
    {
        if (this._trailing is null
            && this.TrailingContent is not null
            && this.GetTemplateChild("ContentRoot") is Grid root
            && this.GetTemplateChild("MoreButton") is Button more)
        {
            // shortcut: pin utilities right using native template parts; recheck them when upgrading WinUI.
            root.ColumnDefinitions[0].Width = GridLength.Auto;
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            this._trailing = new ContentPresenter { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(this._trailing, this._toolToolbar ? 3 : 2);
            if (!this._toolToolbar)
            {
                this._trailing.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(more, 3);
            }

            root.Children.Insert(root.Children.IndexOf(more) + (this._toolToolbar ? 1 : 0), this._trailing);
            if (this.GetTemplateChild("PrimaryItemsControl") is FrameworkElement { Parent: FrameworkElement actions })
            {
                var utilities = this._trailing;

                void FitActions()
                {
                    if (root.ActualWidth > 0)
                    {
                        actions.MaxWidth = Math.Max(0, root.ActualWidth - utilities.ActualWidth - 40);
                    }
                }

                root.SizeChanged += (_, _) => FitActions();
                utilities.SizeChanged += (_, _) => FitActions();
                FitActions();
            }

            foreach (var name in new[] { "HighContrastBorder", "OpenBorder" })
            {
                if (this.GetTemplateChild(name) is FrameworkElement border)
                {
                    Grid.SetColumnSpan(border, 4);
                }
            }
        }

        if (this._trailing is not null)
        {
            this._trailing.Content = this.TrailingContent;
        }
    }

    private void PositionOverflow()
    {
        if (this._trailing is not null
            && this.GetTemplateChild("OverflowPopup") is Popup popup
            && this.GetTemplateChild("MoreButton") is Button more)
        {
            // Native overflow aligns to the whole bar, including the utilities.
            popup.HorizontalOffset = this.IsOpen
                ? more.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(more.ActualWidth, 0)).X - this.ActualWidth
                : 0;
        }
    }
}
