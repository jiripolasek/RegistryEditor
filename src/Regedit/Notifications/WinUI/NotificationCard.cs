// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace JPSoftworks.Notifications.WinUI;

/// <summary>Presents a host-owned notification through an application-replaceable template.</summary>
/// <remarks>
/// Use <see cref="NotificationHost"/> to supply notification models. The host maintains content, progress, and visibility
/// properties for template bindings; appearance properties can be configured through styles. Use this control on its UI thread.
/// PART_ActionsPresenter hosts generated actions; PART_CloseButton and PART_DismissAction request dismissal.
/// PART_CustomIcon hosts a resolved icon, PART_NativeNotice supports native InfoBar presentation, and PART_Surface receives shadows.
/// Templates may omit parts for features they do not present. Input events request actions or dismissal; this card does not execute
/// them. Middle-clicking anywhere on the card requests dismissal, including over action controls.
/// </remarks>
[TemplatePart(Name = "PART_ActionsPresenter", Type = typeof(ContentPresenter))]
[TemplatePart(Name = "PART_CloseButton", Type = typeof(ButtonBase))]
[TemplatePart(Name = "PART_DismissAction", Type = typeof(ButtonBase))]
[TemplatePart(Name = "PART_CustomIcon", Type = typeof(ContentPresenter))]
[TemplatePart(Name = "PART_NativeNotice", Type = typeof(InfoBar))]
[TemplatePart(Name = "PART_Surface", Type = typeof(UIElement))]
public sealed partial class NotificationCard : Control
{
    /// <summary>Identifies the <see cref="Strings"/> dependency property.</summary>
    public static readonly DependencyProperty StringsProperty = DependencyProperty.Register(
        nameof(Strings), typeof(ResourceDictionary), typeof(NotificationCard),
        new PropertyMetadata(null, Presentation_Changed));

    /// <summary>Identifies the <see cref="DismissNotificationText"/> dependency property.</summary>
    public static readonly DependencyProperty DismissNotificationTextProperty = DependencyProperty.Register(
        nameof(DismissNotificationText), typeof(string), typeof(NotificationCard),
        new PropertyMetadata(NotificationText.Get(null, "DismissNotification")));

    /// <summary>Identifies the <see cref="ProgressText"/> dependency property.</summary>
    public static readonly DependencyProperty ProgressTextProperty = DependencyProperty.Register(
        nameof(ProgressText), typeof(string), typeof(NotificationCard),
        new PropertyMetadata(NotificationText.Get(null, "Progress")));

    /// <summary>Identifies the <see cref="BusyText"/> dependency property.</summary>
    public static readonly DependencyProperty BusyTextProperty = DependencyProperty.Register(
        nameof(BusyText), typeof(string), typeof(NotificationCard),
        new PropertyMetadata(NotificationText.Get(null, "Busy")));

    /// <summary>Identifies the <see cref="Material"/> dependency property.</summary>
    public static readonly DependencyProperty MaterialProperty = DependencyProperty.Register(
        nameof(Material), typeof(NotificationMaterial), typeof(NotificationCard),
        new PropertyMetadata(NotificationMaterial.Solid, Presentation_Changed));

    /// <summary>Identifies the <see cref="ActionPresentation"/> dependency property.</summary>
    public static readonly DependencyProperty ActionPresentationProperty = DependencyProperty.Register(
        nameof(ActionPresentation), typeof(NotificationActionPresentation), typeof(NotificationCard),
        new PropertyMetadata(NotificationActionPresentation.Link, Presentation_Changed));

    /// <summary>Identifies the <see cref="DismissPresentation"/> dependency property.</summary>
    public static readonly DependencyProperty DismissPresentationProperty = DependencyProperty.Register(
        nameof(DismissPresentation), typeof(NotificationDismissPresentation), typeof(NotificationCard),
        new PropertyMetadata(NotificationDismissPresentation.Icon, Presentation_Changed));

    /// <summary>Identifies the <see cref="IsActionRunning"/> dependency property.</summary>
    public static readonly DependencyProperty IsActionRunningProperty = DependencyProperty.Register(
        nameof(IsActionRunning), typeof(bool), typeof(NotificationCard),
        new PropertyMetadata(false, Presentation_Changed));

    /// <summary>Identifies the <see cref="IconForeground"/> dependency property.</summary>
    public static readonly DependencyProperty IconForegroundProperty = DependencyProperty.Register(
        nameof(IconForeground), typeof(Brush), typeof(NotificationCard),
        new PropertyMetadata(null, Presentation_Changed));

    /// <summary>Identifies the <see cref="DetailSpacing"/> dependency property.</summary>
    public static readonly DependencyProperty DetailSpacingProperty = DependencyProperty.Register(
        nameof(DetailSpacing), typeof(double), typeof(NotificationCard),
        new PropertyMetadata(8d, Presentation_Changed));

    /// <summary>Identifies the <see cref="ShadowDepth"/> dependency property.</summary>
    public static readonly DependencyProperty ShadowDepthProperty = DependencyProperty.Register(
        nameof(ShadowDepth), typeof(double), typeof(NotificationCard), new PropertyMetadata(0d, Presentation_Changed));

    /// <summary>Identifies the <see cref="ActionSpacing"/> dependency property.</summary>
    public static readonly DependencyProperty ActionSpacingProperty = DependencyProperty.Register(
        nameof(ActionSpacing), typeof(double), typeof(NotificationCard),
        new PropertyMetadata(12d, Presentation_Changed));

    /// <summary>Identifies the <see cref="ActionPanelStyle"/> dependency property.</summary>
    public static readonly DependencyProperty ActionPanelStyleProperty = DependencyProperty.Register(
        nameof(ActionPanelStyle), typeof(Style), typeof(NotificationCard),
        new PropertyMetadata(null, Presentation_Changed));

    /// <summary>Identifies the <see cref="ActionLinkStyle"/> dependency property.</summary>
    public static readonly DependencyProperty ActionLinkStyleProperty = DependencyProperty.Register(
        nameof(ActionLinkStyle), typeof(Style), typeof(NotificationCard),
        new PropertyMetadata(null, Presentation_Changed));

    /// <summary>Identifies the <see cref="ActionButtonStyle"/> dependency property.</summary>
    public static readonly DependencyProperty ActionButtonStyleProperty = DependencyProperty.Register(
        nameof(ActionButtonStyle), typeof(Style), typeof(NotificationCard),
        new PropertyMetadata(null, Presentation_Changed));

    /// <summary>Identifies the <see cref="DefaultActionLinkStyle"/> dependency property.</summary>
    public static readonly DependencyProperty DefaultActionLinkStyleProperty = DependencyProperty.Register(
        nameof(DefaultActionLinkStyle), typeof(Style), typeof(NotificationCard),
        new PropertyMetadata(null, Presentation_Changed));

    /// <summary>Identifies the <see cref="DefaultActionButtonStyle"/> dependency property.</summary>
    public static readonly DependencyProperty DefaultActionButtonStyleProperty = DependencyProperty.Register(
        nameof(DefaultActionButtonStyle), typeof(Style), typeof(NotificationCard),
        new PropertyMetadata(null, Presentation_Changed));

    /// <summary>Identifies the <see cref="Title"/> dependency property.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(NotificationCard), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Message"/> dependency property.</summary>
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(NotificationCard), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="ProgressLabel"/> dependency property.</summary>
    public static readonly DependencyProperty ProgressLabelProperty = DependencyProperty.Register(
        nameof(ProgressLabel), typeof(string), typeof(NotificationCard), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="DismissLabel"/> dependency property.</summary>
    public static readonly DependencyProperty DismissLabelProperty = DependencyProperty.Register(
        nameof(DismissLabel), typeof(string), typeof(NotificationCard),
        new PropertyMetadata(NotificationText.Get(null, "Dismiss")));

    /// <summary>Identifies the <see cref="ProgressValue"/> dependency property.</summary>
    public static readonly DependencyProperty ProgressValueProperty = DependencyProperty.Register(
        nameof(ProgressValue), typeof(double), typeof(NotificationCard), new PropertyMetadata(0d));

    /// <summary>Identifies the <see cref="Severity"/> dependency property.</summary>
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(NotificationSeverity), typeof(NotificationCard),
        new PropertyMetadata(NotificationSeverity.Neutral));

    /// <summary>Identifies the <see cref="IconSource"/> dependency property.</summary>
    public static readonly DependencyProperty IconSourceProperty = DependencyProperty.Register(
        nameof(IconSource), typeof(IconSource), typeof(NotificationCard), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="BodyVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty BodyVisibilityProperty = DependencyProperty.Register(
        nameof(BodyVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="DetailsVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty DetailsVisibilityProperty = DependencyProperty.Register(
        nameof(DetailsVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="ActionsVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty ActionsVisibilityProperty = DependencyProperty.Register(
        nameof(ActionsVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="StatusVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty StatusVisibilityProperty = DependencyProperty.Register(
        nameof(StatusVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="SeverityIconVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty SeverityIconVisibilityProperty = DependencyProperty.Register(
        nameof(SeverityIconVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="CustomIconVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty CustomIconVisibilityProperty = DependencyProperty.Register(
        nameof(CustomIconVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="BusyVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty BusyVisibilityProperty = DependencyProperty.Register(
        nameof(BusyVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="ProgressVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty ProgressVisibilityProperty = DependencyProperty.Register(
        nameof(ProgressVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="DeterminateProgressVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty DeterminateProgressVisibilityProperty = DependencyProperty.Register(
        nameof(DeterminateProgressVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="ProgressLabelVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty ProgressLabelVisibilityProperty = DependencyProperty.Register(
        nameof(ProgressLabelVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="DismissIconVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty DismissIconVisibilityProperty = DependencyProperty.Register(
        nameof(DismissIconVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="DismissLinkVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty DismissLinkVisibilityProperty = DependencyProperty.Register(
        nameof(DismissLinkVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="SeverityIndicatorVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty SeverityIndicatorVisibilityProperty = DependencyProperty.Register(
        nameof(SeverityIndicatorVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies the <see cref="IsIndeterminate"/> dependency property.</summary>
    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(NotificationCard), new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="HasIcon"/> dependency property.</summary>
    public static readonly DependencyProperty HasIconProperty = DependencyProperty.Register(
        nameof(HasIcon), typeof(bool), typeof(NotificationCard), new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="IsClosable"/> dependency property.</summary>
    public static readonly DependencyProperty IsClosableProperty = DependencyProperty.Register(
        nameof(IsClosable), typeof(bool), typeof(NotificationCard), new PropertyMetadata(true));

    /// <summary>Identifies the <see cref="ContentSpacing"/> dependency property.</summary>
    public static readonly DependencyProperty ContentSpacingProperty = DependencyProperty.Register(
        nameof(ContentSpacing), typeof(double), typeof(NotificationCard), new PropertyMetadata(0d));

    /// <summary>Identifies the <see cref="ActionItemsVisibility"/> dependency property.</summary>
    public static readonly DependencyProperty ActionItemsVisibilityProperty = DependencyProperty.Register(
        nameof(ActionItemsVisibility), typeof(Visibility), typeof(NotificationCard),
        new PropertyMetadata(Visibility.Collapsed));

    private static readonly Notification EmptyNotification
        = new() { Title = string.Empty, Severity = NotificationSeverity.Neutral };

    private readonly ThemeShadow _shadow = new();
    private readonly StackPanel _actionButtons = new();
    private ContentPresenter? _actionsPresenter;
    private ContentPresenter? _customIconPresenter;
    private ButtonBase? _closeButton;
    private ButtonBase? _dismissAction;
    private InfoBar? _nativeNotice;
    private UIElement? _surface;
    private UIElement? _shadowReceiver;
    private Func<string, IconSource?>? _iconFactory;
    private IconSource? _customIcon;
    private Brush? _iconTint;
    private ElementTheme _iconTheme;
    private Guid _id = Guid.NewGuid();
    private bool _updating;
    private Control? _pendingFocus;
    private RoutedEventHandler? _focusLoaded;

    /// <summary>Initializes a notification card on the current UI thread.</summary>
    public NotificationCard()
    {
        this.DefaultStyleKey = typeof(NotificationCard);
        this.KeyDown += this.Card_KeyDown;
        this.AddHandler(PointerPressedEvent, new PointerEventHandler(this.Card_PointerPressed), true);
        this.ActualThemeChanged += (_, _) => this.UpdatePresentation(forceIcon: true);
        this.Unloaded += (_, _) => this.ClearPendingFocus();
    }

    /// <summary>Gets or sets localized card text and automation labels.</summary>
    /// <remarks>
    /// Missing or non-string resources and malformed formats use built-in English defaults. Assign a new dictionary
    /// on the UI thread to refresh the card. An explicit <see cref="NotificationHost.Strings"/> value overrides a card-style value.
    /// </remarks>
    public ResourceDictionary? Strings
    {
        get => (ResourceDictionary?)this.GetValue(StringsProperty);
        set => this.SetValue(StringsProperty, value);
    }

    /// <summary>Gets the localized accessible name for icon dismissal.</summary>
    public string DismissNotificationText
    {
        get => (string)this.GetValue(DismissNotificationTextProperty);
        private set => this.SetValue(DismissNotificationTextProperty, value);
    }

    /// <summary>Gets the localized accessible name for determinate progress.</summary>
    public string ProgressText
    {
        get => (string)this.GetValue(ProgressTextProperty);
        private set => this.SetValue(ProgressTextProperty, value);
    }

    /// <summary>Gets the localized accessible name for indeterminate progress.</summary>
    public string BusyText
    {
        get => (string)this.GetValue(BusyTextProperty);
        private set => this.SetValue(BusyTextProperty, value);
    }

    /// <summary>Gets or sets the style applied to the generated action StackPanel.</summary>
    public Style? ActionPanelStyle
    {
        get => (Style?)this.GetValue(ActionPanelStyleProperty);
        set => this.SetValue(ActionPanelStyleProperty, value);
    }

    /// <summary>Gets the effective detail spacing, or zero when the detail region is empty.</summary>
    public double ContentSpacing
    {
        get => (double)this.GetValue(ContentSpacingProperty);
        private set => this.SetValue(ContentSpacingProperty, value);
    }

    /// <summary>Gets the visibility of application action controls, excluding text dismissal.</summary>
    public Visibility ActionItemsVisibility
    {
        get => (Visibility)this.GetValue(ActionItemsVisibilityProperty);
        private set => this.SetValue(ActionItemsVisibilityProperty, value);
    }

    /// <summary>Gets or sets the card template's material state.</summary>
    /// <remarks>
    /// Defaults to <see cref="NotificationMaterial.Solid"/>. The default templates use system acrylic resources
    /// for <see cref="NotificationMaterial.Acrylic"/> and a solid fallback in High Contrast.
    /// </remarks>
    public NotificationMaterial Material
    {
        get => (NotificationMaterial)this.GetValue(MaterialProperty);
        set => this.SetValue(MaterialProperty, value);
    }

    /// <summary>Gets or sets whether generated actions use links or buttons.</summary>
    /// <remarks>
    /// Defaults to <see cref="NotificationActionPresentation.Link"/>.
    /// </remarks>
    public NotificationActionPresentation ActionPresentation
    {
        get => (NotificationActionPresentation)this.GetValue(ActionPresentationProperty);
        set => this.SetValue(ActionPresentationProperty, value);
    }

    /// <summary>Gets or sets the default icon or text dismissal presentation.</summary>
    /// <remarks>
    /// Defaults to <see cref="NotificationDismissPresentation.Icon"/>. A notification's explicit dismissal label always selects text
    /// dismissal.
    /// </remarks>
    public NotificationDismissPresentation DismissPresentation
    {
        get => (NotificationDismissPresentation)this.GetValue(DismissPresentationProperty);
        set => this.SetValue(DismissPresentationProperty, value);
    }

    /// <summary>Gets or sets whether an action for this entry is running.</summary>
    /// <remarks>
    /// The host updates this state from session snapshots. It disables action controls and default-action keyboard activation.
    /// </remarks>
    public bool IsActionRunning
    {
        get => (bool)this.GetValue(IsActionRunningProperty);
        set => this.SetValue(IsActionRunningProperty, value);
    }

    /// <summary>Gets or sets the fallback foreground for custom icons.</summary>
    /// <remarks>
    /// It applies only when a resolved icon source has no explicit foreground. Default styles use a neutral theme brush, independent of
    /// severity.
    /// </remarks>
    public Brush? IconForeground
    {
        get => (Brush?)this.GetValue(IconForegroundProperty);
        set => this.SetValue(IconForegroundProperty, value);
    }

    /// <summary>Gets or sets the gap between title and detail content, in effective pixels.</summary>
    /// <remarks>
    /// The dependency-property default is 8.
    /// </remarks>
    public double DetailSpacing
    {
        get => (double)this.GetValue(DetailSpacingProperty);
        set => this.SetValue(DetailSpacingProperty, value);
    }

    /// <summary>Gets or sets the surface's translation depth when a shadow receiver is supplied.</summary>
    /// <remarks>
    /// The dependency-property default is zero; default appearance styles supply their own depth.
    /// </remarks>
    public double ShadowDepth
    {
        get => (double)this.GetValue(ShadowDepthProperty);
        set => this.SetValue(ShadowDepthProperty, value);
    }

    /// <summary>Gets or sets the gap between generated actions, in effective pixels.</summary>
    /// <remarks>
    /// The dependency-property default is 12.
    /// </remarks>
    public double ActionSpacing
    {
        get => (double)this.GetValue(ActionSpacingProperty);
        set => this.SetValue(ActionSpacingProperty, value);
    }

    /// <summary>Gets or sets the style for non-default action HyperlinkButton controls.</summary>
    public Style? ActionLinkStyle
    {
        get => (Style?)this.GetValue(ActionLinkStyleProperty);
        set => this.SetValue(ActionLinkStyleProperty, value);
    }

    /// <summary>Gets or sets the style for non-default action Button controls.</summary>
    public Style? ActionButtonStyle
    {
        get => (Style?)this.GetValue(ActionButtonStyleProperty);
        set => this.SetValue(ActionButtonStyleProperty, value);
    }

    /// <summary>Gets or sets the style for the default action when presented as a HyperlinkButton.</summary>
    public Style? DefaultActionLinkStyle
    {
        get => (Style?)this.GetValue(DefaultActionLinkStyleProperty);
        set => this.SetValue(DefaultActionLinkStyleProperty, value);
    }

    /// <summary>Gets or sets the style for the default action when presented as a Button.</summary>
    public Style? DefaultActionButtonStyle
    {
        get => (Style?)this.GetValue(DefaultActionButtonStyleProperty);
        set => this.SetValue(DefaultActionButtonStyleProperty, value);
    }

    /// <summary>Gets the current notification title for template bindings.</summary>
    public string Title
    {
        get => (string)this.GetValue(TitleProperty);
        private set => this.SetValue(TitleProperty, value);
    }

    /// <summary>Gets the current notification body for template bindings.</summary>
    public string Message
    {
        get => (string)this.GetValue(MessageProperty);
        private set => this.SetValue(MessageProperty, value);
    }

    /// <summary>Gets the localized progress text or indeterminate-progress message.</summary>
    public string ProgressLabel
    {
        get => (string)this.GetValue(ProgressLabelProperty);
        private set => this.SetValue(ProgressLabelProperty, value);
    }

    /// <summary>Gets the instance dismissal label, or the localized default when none is supplied.</summary>
    public string DismissLabel
    {
        get => (string)this.GetValue(DismissLabelProperty);
        private set => this.SetValue(DismissLabelProperty, value);
    }

    /// <summary>Gets the completion fraction from zero to one, or zero when no fraction is available.</summary>
    public double ProgressValue
    {
        get => (double)this.GetValue(ProgressValueProperty);
        private set => this.SetValue(ProgressValueProperty, value);
    }

    /// <summary>Gets the current notification's semantic severity.</summary>
    public NotificationSeverity Severity
    {
        get => (NotificationSeverity)this.GetValue(SeverityProperty);
        private set => this.SetValue(SeverityProperty, value);
    }

    /// <summary>Gets the resolved custom icon source, or null when the severity default is used.</summary>
    public IconSource? IconSource
    {
        get => (IconSource?)this.GetValue(IconSourceProperty);
        private set => this.SetValue(IconSourceProperty, value);
    }

    /// <summary>Gets the visibility of the non-progress message body.</summary>
    public Visibility BodyVisibility
    {
        get => (Visibility)this.GetValue(BodyVisibilityProperty);
        private set => this.SetValue(BodyVisibilityProperty, value);
    }

    /// <summary>Gets whether the detail region contains message, progress, actions, or text dismissal.</summary>
    public Visibility DetailsVisibility
    {
        get => (Visibility)this.GetValue(DetailsVisibilityProperty);
        private set => this.SetValue(DetailsVisibilityProperty, value);
    }

    /// <summary>Gets the visibility of the combined application-action and text-dismissal region.</summary>
    public Visibility ActionsVisibility
    {
        get => (Visibility)this.GetValue(ActionsVisibilityProperty);
        private set => this.SetValue(ActionsVisibilityProperty, value);
    }

    /// <summary>Gets the visibility of the non-indeterminate status-icon region.</summary>
    public Visibility StatusVisibility
    {
        get => (Visibility)this.GetValue(StatusVisibilityProperty);
        private set => this.SetValue(StatusVisibilityProperty, value);
    }

    /// <summary>Gets whether the default severity icon is selected instead of a custom icon.</summary>
    public Visibility SeverityIconVisibility
    {
        get => (Visibility)this.GetValue(SeverityIconVisibilityProperty);
        private set => this.SetValue(SeverityIconVisibilityProperty, value);
    }

    /// <summary>Gets whether a resolved custom icon is selected.</summary>
    public Visibility CustomIconVisibility
    {
        get => (Visibility)this.GetValue(CustomIconVisibilityProperty);
        private set => this.SetValue(CustomIconVisibilityProperty, value);
    }

    /// <summary>Gets the visibility of the indeterminate-progress spinner.</summary>
    public Visibility BusyVisibility
    {
        get => (Visibility)this.GetValue(BusyVisibilityProperty);
        private set => this.SetValue(BusyVisibilityProperty, value);
    }

    /// <summary>Gets whether the notification contains any progress state.</summary>
    public Visibility ProgressVisibility
    {
        get => (Visibility)this.GetValue(ProgressVisibilityProperty);
        private set => this.SetValue(ProgressVisibilityProperty, value);
    }

    /// <summary>Gets the visibility of progress with a completion fraction.</summary>
    public Visibility DeterminateProgressVisibility
    {
        get => (Visibility)this.GetValue(DeterminateProgressVisibilityProperty);
        private set => this.SetValue(DeterminateProgressVisibilityProperty, value);
    }

    /// <summary>Gets the visibility of progress message or percentage text.</summary>
    public Visibility ProgressLabelVisibility
    {
        get => (Visibility)this.GetValue(ProgressLabelVisibilityProperty);
        private set => this.SetValue(ProgressLabelVisibilityProperty, value);
    }

    /// <summary>Gets whether icon dismissal is selected.</summary>
    public Visibility DismissIconVisibility
    {
        get => (Visibility)this.GetValue(DismissIconVisibilityProperty);
        private set => this.SetValue(DismissIconVisibilityProperty, value);
    }

    /// <summary>Gets whether text dismissal is selected.</summary>
    public Visibility DismissLinkVisibility
    {
        get => (Visibility)this.GetValue(DismissLinkVisibilityProperty);
        private set => this.SetValue(DismissLinkVisibilityProperty, value);
    }

    /// <summary>Gets whether a non-neutral severity indicator is selected.</summary>
    public Visibility SeverityIndicatorVisibility
    {
        get => (Visibility)this.GetValue(SeverityIndicatorVisibilityProperty);
        private set => this.SetValue(SeverityIndicatorVisibilityProperty, value);
    }

    /// <summary>Gets whether the notification has progress without a completion fraction.</summary>
    public bool IsIndeterminate
    {
        get => (bool)this.GetValue(IsIndeterminateProperty);
        private set => this.SetValue(IsIndeterminateProperty, value);
    }

    /// <summary>Gets whether a custom or non-neutral severity icon is available.</summary>
    public bool HasIcon
    {
        get => (bool)this.GetValue(HasIconProperty);
        private set => this.SetValue(HasIconProperty, value);
    }

    /// <summary>Gets whether the template should offer an icon close button.</summary>
    public bool IsClosable
    {
        get => (bool)this.GetValue(IsClosableProperty);
        private set => this.SetValue(IsClosableProperty, value);
    }

    internal Notification? ContentModel { get; private set; }

    /// <summary>Occurs when a dismissal control or a middle-click anywhere on the card requests closure.</summary>
    /// <remarks>
    /// Raised on the UI thread. The host dismisses the entry; dismissal does not cancel a running action.
    /// </remarks>
    public event Action? DismissRequested;

    /// <summary>Occurs when the user invokes a current notification action.</summary>
    /// <remarks>
    /// The argument is the action instance supplied by the notification. Raised on the UI thread for action controls
    /// and Enter on a focused card with a default action. The host executes the action through the session; the card itself does not
    /// run it.
    /// </remarks>
    public event Action<NotificationAction>? ActionRequested;

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        this.UnhookTemplate();
        base.OnApplyTemplate();
        this._actionsPresenter = this.GetTemplateChild("PART_ActionsPresenter") as ContentPresenter;
        this._customIconPresenter = this.GetTemplateChild("PART_CustomIcon") as ContentPresenter;
        this._closeButton = this.GetTemplateChild("PART_CloseButton") as ButtonBase;
        this._dismissAction = this.GetTemplateChild("PART_DismissAction") as ButtonBase;
        this._nativeNotice = this.GetTemplateChild("PART_NativeNotice") as InfoBar;
        this._surface = this.GetTemplateChild("PART_Surface") as UIElement;
        foreach (var button in this.DismissButtons())
        {
            button.Click += this.Dismiss_Click;
        }

        if (this._nativeNotice is not null)
        {
            this._nativeNotice.Closed += this.NativeNotice_Closed;
        }

        this._actionsPresenter?.Content = this._actionButtons;
        this.UpdatePresentation(forceIcon: true);
    }

    private IEnumerable<ButtonBase> DismissButtons() =>
        new[] { this._closeButton, this._dismissAction }.OfType<ButtonBase>();

    private void UnhookTemplate()
    {
        foreach (var button in this.DismissButtons())
        {
            button.Click -= this.Dismiss_Click;
        }

        if (this._nativeNotice is not null)
        {
            this._nativeNotice.Closed -= this.NativeNotice_Closed;
        }

        this._actionsPresenter?.Content = null;
        this._surface?.Shadow = null;
    }

    private void NativeNotice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => this.DismissRequested?.Invoke();

    private static void Presentation_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (NotificationCard)sender;
        if (args.NewValue is Enum value && !Enum.IsDefined(value.GetType(), value))
        {
            card.SetValue(args.Property, args.OldValue);
            throw new ArgumentOutOfRangeException(nameof(args));
        }

        if (!card._updating)
        {
            card.UpdatePresentation(forceIcon: args.Property == IconForegroundProperty);
        }
    }

    internal void ApplyCardStyle(Style? style)
    {
        if (style is null
                ? this.ReadLocalValue(StyleProperty) == DependencyProperty.UnsetValue
                : ReferenceEquals(this.Style, style))
        {
            return;
        }

        this.ClearPendingFocus();
        var focused = this.XamlRoot is null ? null : FocusManager.GetFocusedElement(this.XamlRoot) as Control;
        var restore = focused is not null && this._actionButtons.Children.Contains(focused);
        if (style is null)
        {
            this.ClearValue(StyleProperty);
        }
        else
        {
            this.Style = style;
        }

        this.ApplyTemplate();
        if (!restore)
        {
            return;
        }

        var fallback = FocusManager.GetFocusedElement(this.XamlRoot);
        var template = this.Template;

        void Restore()
        {
            if (this.IsLoaded && ReferenceEquals(this.Template, template) && focused!.IsLoaded &&
                this._actionButtons.Children.Contains(focused) && ReferenceEquals(
                    FocusManager.GetFocusedElement(this.XamlRoot), fallback))
            {
                focused.Focus(FocusState.Programmatic);
            }
        }

        if (focused!.IsLoaded)
        {
            this.DispatcherQueue.TryEnqueue(Restore);
        }
        else
        {
            this._pendingFocus = focused;
            this._focusLoaded = (_, _) =>
            {
                this.ClearPendingFocus();
                this.DispatcherQueue.TryEnqueue(Restore);
            };
            focused.Loaded += this._focusLoaded;
        }
    }

    private void ClearPendingFocus()
    {
        if (this._pendingFocus is not null && this._focusLoaded is not null)
        {
            this._pendingFocus.Loaded -= this._focusLoaded;
        }

        this._pendingFocus = null;
        this._focusLoaded = null;
    }

    internal void Update(
        NotificationSnapshot snapshot,
        UIElement? shadowReceiver,
        Func<string, IconSource?>? iconFactory,
        bool announce = true)
    {
        var previous = this.ContentModel;
        this.ContentModel = snapshot.Content;
        this._id = snapshot.Id;
        this._updating = true;
        this.IsActionRunning = snapshot.IsActionRunning;
        this._updating = false;
        var factoryChanged = !ReferenceEquals(this._iconFactory, iconFactory);
        this._iconFactory = iconFactory;
        if (!ReferenceEquals(this._shadowReceiver, shadowReceiver))
        {
            this._shadow.Receivers.Clear();
            if (shadowReceiver is not null)
            {
                this._shadow.Receivers.Add(shadowReceiver);
            }

            this._shadowReceiver = shadowReceiver;
        }

        this.UpdatePresentation(factoryChanged || previous?.IconKey != snapshot.Content.IconKey);
        var content = snapshot.Content;
        if (announce && (previous is null || previous.Title != content.Title || previous.Severity != content.Severity ||
                         content.Progress is null && previous.Message != content.Message))
        {
            var peer = FrameworkElementAutomationPeer.FromElement(this) ??
                       FrameworkElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                content.Severity is NotificationSeverity.Error or NotificationSeverity.Warning
                    ? AutomationNotificationProcessing.ImportantMostRecent
                    : AutomationNotificationProcessing.CurrentThenMostRecent,
                content.Title + Environment.NewLine + content.Message, "InAppNotification");
        }
    }

    private void UpdatePresentation(bool forceIcon = false)
    {
        var content = this.ContentModel ?? EmptyNotification;
        this.Title = content.Title;
        this.Message = content.Message;
        this.Severity = content.Severity;
        this.DismissLabel = content.DismissLabel ?? NotificationText.Get(this.Strings, "Dismiss");
        this.DismissNotificationText = NotificationText.Get(this.Strings, "DismissNotification");
        this.ProgressText = NotificationText.Get(this.Strings, "Progress");
        this.BusyText = NotificationText.Get(this.Strings, "Busy");
        this.ProgressValue = content.Progress?.Value ?? 0;
        this.ProgressLabel = content.Progress?.Value is { } value
            ? content.Message.Length > 0
                ? NotificationText.Format(this.Strings, "ProgressWithMessageFormat", content.Message, value)
                : NotificationText.Format(this.Strings, "ProgressFormat", value)
            : content.Message;
        if (forceIcon || this._iconTheme != this.ActualTheme || !ReferenceEquals(this._iconTint, this.IconForeground))
        {
            this._iconTheme = this.ActualTheme;
            this._iconTint = this.IconForeground;
            this._customIcon = content.IconKey is { } key ? this._iconFactory?.Invoke(key) : null;
            if (this._customIcon is { Foreground: null } source)
            {
                BindingOperations.SetBinding(
                    source, IconSource.ForegroundProperty,
                    new Binding
                    {
                        Source = this,
                        Path = new PropertyPath(nameof(this.IconForeground)),
                        Mode = BindingMode.OneWay
                    });
            }

            this.IconSource = this._customIcon;
            // Replace the element because clearing a retained IconSourceElement can fail in WinUI.
            IconSourceElement? icon = this._customIcon is null
                ? null
                : new IconSourceElement { IconSource = this._customIcon };
            if (icon is not null)
            {
                AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            }

            this._customIconPresenter?.Content = icon;
        }

        var custom = this._customIcon is not null;
        var dismissLink = content.DismissLabel is not null ||
                          this.DismissPresentation == NotificationDismissPresentation.Link;
        this.IsIndeterminate = content.Progress is { Value: null };
        this.HasIcon = custom || content.Severity != NotificationSeverity.Neutral;
        this.IsClosable = !dismissLink;
        this.BodyVisibility = Visible(content.Progress is null && content.Message.Length > 0);
        this.ActionsVisibility = Visible(content.Actions.Count > 0 || dismissLink);
        this.DetailsVisibility = Visible(
            content.Message.Length > 0 || content.Progress?.Value is not null || content.Actions.Count > 0 ||
            dismissLink);
        this.ContentSpacing = this.DetailsVisibility == Visibility.Visible ? this.DetailSpacing : 0;
        this.ActionItemsVisibility = Visible(content.Actions.Count > 0);
        this.StatusVisibility = Visible(!this.IsIndeterminate && this.HasIcon);
        this.SeverityIconVisibility = Visible(!custom && content.Severity != NotificationSeverity.Neutral);
        this.CustomIconVisibility = Visible(custom);
        this.BusyVisibility = Visible(this.IsIndeterminate);
        this.ProgressVisibility = Visible(content.Progress is not null);
        this.DeterminateProgressVisibility = Visible(content.Progress?.Value is not null);
        this.ProgressLabelVisibility = Visible(
            content.Progress is not null && (content.Message.Length > 0 || !this.IsIndeterminate));
        this.DismissIconVisibility = Visible(!dismissLink);
        this.DismissLinkVisibility = Visible(dismissLink);
        this.SeverityIndicatorVisibility = Visible(content.Severity != NotificationSeverity.Neutral);
        var suffix = this._id.ToString("N");
        AutomationProperties.SetAutomationId(this, "Notification_" + suffix);
        if (this._closeButton is not null)
        {
            AutomationProperties.SetAutomationId(this._closeButton, "DismissNotification_" + suffix);
        }

        if (this._dismissAction is not null)
        {
            AutomationProperties.SetAutomationId(this._dismissAction, "DismissNotificationLink_" + suffix);
        }

        this.IsTabStop = content.Actions.Any(action => action.IsDefault);
        this.UpdateActions(content, suffix);
        VisualStateManager.GoToState(this, this.Material.ToString(), false);
        VisualStateManager.GoToState(this, "Severity" + content.Severity, false);
        this._surface?.Shadow = this._shadowReceiver is null ? null : this._shadow;
        this._surface?.Translation
            = this._shadowReceiver is null ? Vector3.Zero : new Vector3(0, 0, (float)this.ShadowDepth);
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateActions(Notification content, string suffix)
    {
        this._actionButtons.Style = this.ActionPanelStyle;
        this._actionButtons.Spacing = this.ActionSpacing;
        for (var index = 0; index < content.Actions.Count; index++)
        {
            var action = content.Actions[index];
            var link = this.ActionPresentation == NotificationActionPresentation.Link;
            var existing = index < this._actionButtons.Children.Count
                ? this._actionButtons.Children[index] as ButtonBase
                : null;
            if (existing is null || link != (existing is HyperlinkButton))
            {
                ButtonBase button = link ? new HyperlinkButton() : new Button();
                button.Click += (_, _) => this.ActionRequested?.Invoke((NotificationAction)button.Tag);
                if (existing is not null)
                {
                    this._actionButtons.Children.RemoveAt(index);
                }

                this._actionButtons.Children.Insert(index, button);
                existing = button;
            }

            existing.Content = action.Label;
            existing.Tag = action;
            existing.IsEnabled = !this.IsActionRunning;
            existing.Style = link ? action.IsDefault ? this.DefaultActionLinkStyle : this.ActionLinkStyle :
                action.IsDefault ? this.DefaultActionButtonStyle : this.ActionButtonStyle;
            AutomationProperties.SetAutomationId(existing, $"NotificationAction_{suffix}_{index}");
            AutomationProperties.SetHelpText(
                existing, action.IsDefault ? NotificationText.Get(this.Strings, "DefaultAction") : string.Empty);
        }

        while (this._actionButtons.Children.Count > content.Actions.Count)
        {
            this._actionButtons.Children.RemoveAt(this._actionButtons.Children.Count - 1);
        }
    }

    private void Card_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || this.IsActionRunning ||
            this.ContentModel?.Actions.FirstOrDefault(action => action.IsDefault) is not { } action)
        {
            return;
        }

        for (var source = args.OriginalSource as DependencyObject;
             source is not null && !ReferenceEquals(source, this);
             source = VisualTreeHelper.GetParent(source))
        {
            if (source is ButtonBase)
            {
                return;
            }
        }

        args.Handled = true;
        this.ActionRequested?.Invoke(action);
    }

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (args.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed)
        {
            return;
        }

        args.Handled = true;
        this.DismissRequested?.Invoke();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs args) => this.DismissRequested?.Invoke();

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new NotificationCardAutomationPeer(this);

    private sealed partial class NotificationCardAutomationPeer : FrameworkElementAutomationPeer
    {
        private readonly NotificationCard _owner;

        internal NotificationCardAutomationPeer(NotificationCard owner)
            : base(owner)
        {
            this._owner = owner;
        }

        protected override string GetClassNameCore() => nameof(NotificationCard);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetNameCore() => this._owner.ContentModel is { } content
            ? NotificationText.Name(this._owner.Strings, content)
            : string.Empty;
    }
}
