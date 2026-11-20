// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Diagnostics;
using JPSoftworks.Notifications;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace JPSoftworks.Notifications.WinUI;

/// <summary>Presents an attached notification session as an overlay within a bounded region.</summary>
/// <remarks>
/// Create, configure, attach, and dispose this control on its UI thread. The owner disposes the session separately.
/// Place the host last in a bounded grid cell; it reports zero desired size so notifications do not resize underlying content.
/// The host pauses expiry while inactive, expanded, or interacting, and marshals session changes to its dispatcher.
/// Templates require PART_HostStack, PART_StackDeck, PART_Footer, PART_CardsPanel, PART_CardsViewport, and PART_StackToggle
/// with the declared types. Other declared parts support optional clipping, scroll-edge masking, and stack backplates.
/// </remarks>
[TemplatePart(Name = "PART_HostStack", Type = typeof(Grid))]
[TemplatePart(Name = "PART_StackDeck", Type = typeof(Grid))]
[TemplatePart(Name = "PART_Footer", Type = typeof(Grid))]
[TemplatePart(Name = "PART_CardsPanel", Type = typeof(StackPanel))]
[TemplatePart(Name = "PART_CardsViewport", Type = typeof(ScrollViewer))]
[TemplatePart(Name = "PART_StackToggle", Type = typeof(ToggleButton))]
[TemplatePart(Name = "PART_HostClip", Type = typeof(RectangleGeometry))]
[TemplatePart(Name = "PART_ScrollSurface", Type = typeof(Grid))]
[TemplatePart(Name = "PART_ScrollFadeMask", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_BackplateDirection", Type = typeof(ScaleTransform))]
[TemplatePart(Name = "PART_PeekNear", Type = typeof(Border))]
[TemplatePart(Name = "PART_PeekFar", Type = typeof(Border))]
[TemplatePart(Name = "PART_PeekExpand", Type = typeof(Button))]
public sealed partial class NotificationHost : Control, IDisposable
{
    /// <summary>Occurs when the host observes a notification action failure.</summary>
    /// <remarks>
    /// The argument is the callback exception. The session retains the matching failed revision for retry.
    /// The event follows the awaiting caller's continuation context; actions invoked from this host's UI use the UI thread.
    /// Cancellation observed after host disposal is suppressed.
    /// </remarks>
    public event Action<Exception>? ActionFailed;

    /// <summary>Identifies the <see cref="Strings"/> dependency property.</summary>
    public static readonly DependencyProperty StringsProperty = DependencyProperty.Register(
        nameof(Strings), typeof(ResourceDictionary), typeof(NotificationHost), new PropertyMetadata(null, Strings_Changed));

    /// <summary>Identifies the <see cref="DismissText"/> dependency property.</summary>
    public static readonly DependencyProperty DismissTextProperty = DependencyProperty.Register(
        nameof(DismissText), typeof(string), typeof(NotificationHost), new PropertyMetadata(NotificationText.Get(null, "Dismiss")));

    /// <summary>Identifies the <see cref="DismissAllText"/> dependency property.</summary>
    public static readonly DependencyProperty DismissAllTextProperty = DependencyProperty.Register(
        nameof(DismissAllText), typeof(string), typeof(NotificationHost), new PropertyMetadata(NotificationText.Get(null, "DismissAll")));

    /// <summary>Identifies the <see cref="ExpandStackText"/> dependency property.</summary>
    public static readonly DependencyProperty ExpandStackTextProperty = DependencyProperty.Register(
        nameof(ExpandStackText), typeof(string), typeof(NotificationHost), new PropertyMetadata(NotificationText.Get(null, "ExpandStack")));

    /// <summary>Identifies the <see cref="Position"/> dependency property.</summary>
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(NotificationPosition), typeof(NotificationHost),
        new PropertyMetadata(NotificationPosition.BottomRight, Position_Changed));

    /// <summary>Identifies the <see cref="ActiveCount"/> dependency property.</summary>
    public static readonly DependencyProperty ActiveCountProperty = DependencyProperty.Register(
        nameof(ActiveCount), typeof(int), typeof(NotificationHost), new PropertyMetadata(0));

    /// <summary>Identifies the <see cref="CountText"/> dependency property.</summary>
    public static readonly DependencyProperty CountTextProperty = DependencyProperty.Register(
        nameof(CountText), typeof(string), typeof(NotificationHost), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Material"/> dependency property.</summary>
    public static readonly DependencyProperty MaterialProperty = DependencyProperty.Register(
        nameof(Material), typeof(NotificationMaterial), typeof(NotificationHost),
        new PropertyMetadata(NotificationMaterial.Solid, Presentation_Changed));

    /// <summary>Identifies the <see cref="CardSpacing"/> dependency property.</summary>
    public static readonly DependencyProperty CardSpacingProperty = DependencyProperty.Register(
        nameof(CardSpacing), typeof(double), typeof(NotificationHost), new PropertyMetadata(16d, Presentation_Changed));

    /// <summary>Identifies the <see cref="CardStyle"/> dependency property.</summary>
    public static readonly DependencyProperty CardStyleProperty = DependencyProperty.Register(
        nameof(CardStyle), typeof(Style), typeof(NotificationHost), new PropertyMetadata(null, Presentation_Changed));

    /// <summary>Identifies the <see cref="IsExpanded"/> dependency property.</summary>
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(NotificationHost), new PropertyMetadata(false, Expanded_Changed));

    /// <summary>Identifies the <see cref="HistoryTemplate"/> dependency property.</summary>
    public static readonly DependencyProperty HistoryTemplateProperty = DependencyProperty.Register(
        nameof(HistoryTemplate), typeof(DataTemplate), typeof(NotificationHost), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="HistoryItemTemplate"/> dependency property.</summary>
    public static readonly DependencyProperty HistoryItemTemplateProperty = DependencyProperty.Register(
        nameof(HistoryItemTemplate), typeof(DataTemplate), typeof(NotificationHost), new PropertyMetadata(null));

    private readonly Flyout _history = new();
    private readonly Dictionary<Guid, Card> _cards = [];
    private readonly Dictionary<Guid, HistoryEntry> _historyRows = [];
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _peekHover = new();
    private readonly UISettings _uiSettings = new();
    private ListView? _historyList;
    private TextBlock? _historyOmitted;
    private NotificationSession? _session;
    private Func<string, IconSource?>? _iconFactory;
    private UIElement? _shadowReceiver;
    private bool _windowActive = true;
    private bool _disposed;
    private int _renderQueued;
    private bool _scrollToFront;
    private bool _templateReady;
    private bool _isStackHovered;
    private WeakReference<Control>? _returnFocus;

    private RectangleGeometry? _hostClip;
    private ScaleTransform? _backplateDirection;
    private Border? _peekNear;
    private Border? _peekFar;
    private Button? _peekExpand;
    private Grid _hostStack = null!;
    private Grid _stackDeck = null!;
    private Grid _footer = null!;
    private StackPanel _cardsPanel = null!;
    private ScrollViewer _cardsViewport = null!;
    private ToggleButton _stackToggle = null!;

    /// <summary>Gets or sets localized notification text forwarded to generated cards.</summary>
    /// <remarks>
    /// Missing or non-string resources and malformed formats use built-in English defaults. Assign a new dictionary
    /// on the UI thread to refresh existing views. A non-null host value takes precedence over card-style dictionaries.
    /// </remarks>
    public ResourceDictionary? Strings
    {
        get => (ResourceDictionary?)this.GetValue(StringsProperty);
        set => this.SetValue(StringsProperty, value);
    }

    /// <summary>Gets the localized dismissal label for template bindings.</summary>
    public string DismissText
    {
        get => (string)this.GetValue(DismissTextProperty);
        private set => this.SetValue(DismissTextProperty, value);
    }

    /// <summary>Gets the localized dismiss-all label for history template bindings.</summary>
    public string DismissAllText
    {
        get => (string)this.GetValue(DismissAllTextProperty);
        private set => this.SetValue(DismissAllTextProperty, value);
    }

    /// <summary>Gets the localized expansion label for template bindings.</summary>
    public string ExpandStackText
    {
        get => (string)this.GetValue(ExpandStackTextProperty);
        private set => this.SetValue(ExpandStackTextProperty, value);
    }

    /// <summary>Gets the attached session's total active count, including entries hidden by a collapsed stack.</summary>
    public int ActiveCount
    {
        get => (int)this.GetValue(ActiveCountProperty);
        private set => this.SetValue(ActiveCountProperty, value);
    }

    /// <summary>Gets the active count formatted for stack template bindings.</summary>
    public string CountText
    {
        get => (string)this.GetValue(CountTextProperty);
        private set => this.SetValue(CountTextProperty, value);
    }

    /// <summary>Gets or sets the stack's anchor within the host bounds.</summary>
    /// <remarks>
    /// Defaults to <see cref="NotificationPosition.BottomRight"/>. Position is relative to the host, not the screen.
    /// </remarks>
    public NotificationPosition Position
    {
        get => (NotificationPosition)this.GetValue(PositionProperty);
        set => this.SetValue(PositionProperty, value);
    }

    private bool IsTop => this.Position is NotificationPosition.TopLeft or NotificationPosition.TopCenter or NotificationPosition.TopRight;

    /// <summary>Gets or sets the factory that resolves notification icon keys.</summary>
    /// <remarks>
    /// The factory runs on the UI thread, including after theme changes. Return a fresh renderer-owned source per call,
    /// or null to use the severity default. Sources without an explicit foreground receive the card's neutral theme brush.
    /// </remarks>
    public Func<string, IconSource?>? IconFactory
    {
        get => this._iconFactory;
        set
        {
            this._iconFactory = value;
            this.RequestRender();
        }
    }

    /// <summary>Gets or sets the material state of the host template.</summary>
    /// <remarks>
    /// Defaults to <see cref="NotificationMaterial.Solid"/>. Default host templates apply this to backplates;
    /// configure generated card material separately through <see cref="CardStyle"/>.
    /// </remarks>
    public NotificationMaterial Material
    {
        get => (NotificationMaterial)this.GetValue(MaterialProperty);
        set => this.SetValue(MaterialProperty, value);
    }

    /// <summary>Gets or sets the gap between generated cards, in effective pixels.</summary>
    /// <remarks>
    /// The dependency-property default is 16; application styles may override it.
    /// </remarks>
    public double CardSpacing
    {
        get => (double)this.GetValue(CardSpacingProperty);
        set => this.SetValue(CardSpacingProperty, value);
    }

    /// <summary>Gets or sets an optional explicit style for generated cards.</summary>
    /// <remarks>
    /// A <see langword="null"/> value leaves cards to normal implicit-style lookup. The host preserves existing cards
    /// when styles or templates change. Card-level material and action/dismissal appearance belong in this style.
    /// </remarks>
    public Style? CardStyle
    {
        get => (Style?)this.GetValue(CardStyleProperty);
        set => this.SetValue(CardStyleProperty, value);
    }

    /// <summary>Gets or sets whether the notification stack is expanded.</summary>
    /// <remarks>
    /// Expansion is available when more than two entries are active and pauses expiry while reviewing the stack.
    /// The host resets this value when no longer needed or when attaching a session.
    /// </remarks>
    public bool IsExpanded
    {
        get => (bool)this.GetValue(IsExpandedProperty);
        set => this.SetValue(IsExpandedProperty, value);
    }

    /// <summary>Gets or sets the template for the optional history flyout.</summary>
    /// <remarks>
    /// Its root must be a FrameworkElement containing PART_HistoryList as a ListView. Optional PART_HistoryDismissAll
    /// as ButtonBase and PART_HistoryOmitted as TextBlock enable clearing and omitted-entry feedback.
    /// The root data context is this host. The application supplies a command that calls <see cref="ShowHistory"/>.
    /// Template changes take effect when history is next opened.
    /// </remarks>
    public DataTemplate? HistoryTemplate
    {
        get => (DataTemplate?)this.GetValue(HistoryTemplateProperty);
        set => this.SetValue(HistoryTemplateProperty, value);
    }

    /// <summary>Gets or sets the template for each history entry.</summary>
    /// <remarks>
    /// The root must be a FrameworkElement containing PART_HistoryTitle and PART_HistoryMessage as TextBlock,
    /// PART_HistoryDismiss as ButtonBase, and PART_HistoryActions as Panel. The root data context is this host.
    /// Missing required parts prevent that row from being presented. Existing rows retain their template until history is reopened.
    /// </remarks>
    public DataTemplate? HistoryItemTemplate
    {
        get => (DataTemplate?)this.GetValue(HistoryItemTemplateProperty);
        set => this.SetValue(HistoryItemTemplateProperty, value);
    }

    /// <summary>Gets or sets an optional element receiving shadows from card surfaces.</summary>
    /// <remarks>
    /// A <see langword="null"/> value disables those card shadows. The receiver must belong to the same XAML visual tree.
    /// </remarks>
    public UIElement? ShadowReceiver
    {
        get => this._shadowReceiver;
        set
        {
            this._shadowReceiver = value;
            this.RequestRender();
        }
    }

    /// <summary>Gets or sets whether the owning window is active.</summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>. Update this from window activation events so expiry and stack-hover behavior
    /// are paused while the window is inactive.
    /// </remarks>
    public bool IsWindowActive
    {
        get => this._windowActive;
        set
        {
            this._windowActive = value;
            this._isStackHovered &= value;
            if (!value)
            {
                this._peekHover.Reset();
            }

            this.UpdateStackFan();
            this._clock.Restart();
        }
    }

    /// <summary>Initializes an unattached notification host on the current UI thread.</summary>
    public NotificationHost()
    {
        this.DefaultStyleKey = typeof(NotificationHost);
        this.ActualThemeChanged += (_, _) => this.RequestRender();
        this._uiSettings.AnimationsEnabledChanged += this.AnimationsEnabled_Changed;
        this.RegisterPropertyChangedCallback(PaddingProperty, (_, _) => this.UpdateBounds());
        this.GettingFocus += (_, args) =>
        {
            if (args.OldFocusedElement is Control previous && !this.Contains(previous))
            {
                this._returnFocus = new WeakReference<Control>(previous);
            }
        };
        this._history.Opening += (_, _) => this.BuildHistory();
        this._history.Closed += (_, _) => this.ClearHistory();
        this.SizeChanged += (_, _) => this.UpdateBounds();
        this._timer = this.DispatcherQueue.CreateTimer();
        this._timer.Interval = TimeSpan.FromMilliseconds(200);
        this._timer.Tick += this.Tick;
        this.Loaded += (_, _) => this.StartPresentation();
        this.Unloaded += (_, _) => this.Detach();
    }

    /// <summary>Releases this host's subscriptions, timer, cards, and history presentation.</summary>
    /// <remarks>
    /// Call on the UI thread. Disposal is idempotent and does not dispose or cancel the attached session.
    /// </remarks>
    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this._uiSettings.AnimationsEnabledChanged -= this.AnimationsEnabled_Changed;
        this.Detach();
        this.UnhookTemplate();
        this._templateReady = false;
        this._timer.Tick -= this.Tick;
        this._history.Hide();
        this.ClearHistory();
        this._cardsPanel?.Children.Clear();
        this._cards.Clear();
        this._session = null;
        this._iconFactory = null;
        this._shadowReceiver = null;
        this.ActionFailed = null;
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        var ordered = this._cards.Values.OrderBy(card => this._cardsPanel?.Children.IndexOf(card.View) ?? -1).ToArray();
        this._templateReady = false;
        this.UnhookTemplate();
        base.OnApplyTemplate();
        this._hostClip = this.GetTemplateChild("PART_HostClip") as RectangleGeometry;
        this._backplateDirection = this.GetTemplateChild("PART_BackplateDirection") as ScaleTransform;
        this._peekNear = this.GetTemplateChild("PART_PeekNear") as Border;
        this._peekFar = this.GetTemplateChild("PART_PeekFar") as Border;
        this._peekExpand = this.GetTemplateChild("PART_PeekExpand") as Button;
        this._hostStack = (this.GetTemplateChild("PART_HostStack") as Grid)!;
        this._stackDeck = (this.GetTemplateChild("PART_StackDeck") as Grid)!;
        this._footer = (this.GetTemplateChild("PART_Footer") as Grid)!;
        this._cardsPanel = (this.GetTemplateChild("PART_CardsPanel") as StackPanel)!;
        this._cardsViewport = (this.GetTemplateChild("PART_CardsViewport") as ScrollViewer)!;
        this._stackToggle = (this.GetTemplateChild("PART_StackToggle") as ToggleButton)!;
        if (this._hostStack is null
            || this._stackDeck is null
            || this._footer is null
            || this._cardsPanel is null
            || this._cardsViewport is null
            || this._stackToggle is null)
        {
            return;
        }

        this._templateReady = true;
        if (this._disposed)
        {
            return;
        }

        this.InitializeScrollFade();
        this._stackDeck.PointerEntered += this.StackDeck_PointerEntered;
        this._stackDeck.PointerExited += this.StackDeck_PointerExited;
        if (this._peekExpand is not null)
        {
            this._peekExpand.Click += this.PeekExpand_Click;
            this._peekExpand.PointerEntered += this.PeekExpand_PointerEntered;
            this._peekExpand.PointerExited += this.PeekExpand_PointerExited;
        }

        this._stackToggle.Click += this.StackToggle_Click;
        this._footer.SizeChanged += this.Footer_SizeChanged;
        foreach (var card in ordered)
        {
            this._cardsPanel.Children.Add(card.View);
        }

        this._stackToggle.IsChecked = this.IsExpanded;
        this._isStackHovered = false;
        this._peekHover.Reset();
        this._clock.Restart();
        this.UpdateBounds();
        this.RequestRender();
    }

    private void UnhookTemplate()
    {
        this.ClearScrollFade();
        if (this._stackDeck is not null)
        {
            this._stackDeck.PointerEntered -= this.StackDeck_PointerEntered;
            this._stackDeck.PointerExited -= this.StackDeck_PointerExited;
        }

        if (this._peekExpand is not null)
        {
            this._peekExpand.Click -= this.PeekExpand_Click;
            this._peekExpand.PointerEntered -= this.PeekExpand_PointerEntered;
            this._peekExpand.PointerExited -= this.PeekExpand_PointerExited;
        }

        if (this._stackToggle is not null)
        {
            this._stackToggle.Click -= this.StackToggle_Click;
        }

        if (this._footer is not null)
        {
            this._footer.SizeChanged -= this.Footer_SizeChanged;
        }

        this._cardsPanel?.Children.Clear();
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // Replacing a template can arrange new parts without changing the host size.
        var arranged = base.ArrangeOverride(finalSize);
        this.UpdateBounds(finalSize);
        return arranged;
    }

    private void UpdateBounds()
    {
        this.UpdateBounds(new Size(this.ActualWidth, this.ActualHeight));
    }

    private void UpdateBounds(Size size)
    {
        if (!this._templateReady)
        {
            return;
        }

        this._hostStack.Width = Math.Max(0, Math.Min(this._hostStack.MaxWidth, size.Width - this.Padding.Left - this.Padding.Right));
        this._hostClip?.Rect = new Rect(0, 0, size.Width, size.Height);
        this.RequestRender();
    }

    private void StackDeck_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        this._isStackHovered = true;
        this.UpdateStackFan();
    }

    private void StackDeck_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        this._isStackHovered = false;
        this._peekHover.Reset();
        this.UpdateStackFan();
    }

    private void PeekExpand_Click(object sender, RoutedEventArgs args)
    {
        this.SetExpanded(true);
    }

    private void PeekExpand_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        // Layout changes after a manual collapse must not reopen the stack.
        if (!args.IsGenerated && this._windowActive)
        {
            this._peekHover.Restart();
        }
    }

    private void PeekExpand_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        this._peekHover.Reset();
    }

    private void StackToggle_Click(object sender, RoutedEventArgs args)
    {
        this.SetExpanded(this._stackToggle.IsChecked == true);
    }

    private void Footer_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        this.RequestRender();
    }

    private static void Presentation_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((NotificationHost)sender).RequestRender();
    }

    private static void Expanded_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var host = (NotificationHost)sender;
        host._peekHover.Reset();
        host._scrollToFront = (bool)args.NewValue;
        host.RequestRender();
    }

    /// <summary>Opens recent messages from an application-owned command.</summary>
    /// <remarks>
    /// Call on the UI thread with a loaded anchor in the host's window. The flyout uses the history templates
    /// and may extend outside the host bounds. Active rows can be dismissed or invoked; closed rows contain no action callbacks.
    /// </remarks>
    /// <param name="anchor">The element anchoring the history flyout.</param>
    /// <exception cref="ArgumentNullException">The anchor is null.</exception>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void ShowHistory(FrameworkElement anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ObjectDisposedException.ThrowIf(this._disposed, this);
        this._history.ShowAt(anchor);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // An overlay must not influence the size of the content beneath it.
        base.MeasureOverride(availableSize);
        return new Size(0, 0);
    }

    private static void Position_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var host = (NotificationHost)sender;
        if (!Enum.IsDefined((NotificationPosition)args.NewValue))
        {
            host.SetValue(PositionProperty, args.OldValue);
            throw new ArgumentOutOfRangeException(nameof(Position));
        }

        host._peekHover.Reset();
        host._isStackHovered = false;
        host._cardsViewport?.Height = double.NaN;
        host._scrollToFront = host.IsExpanded;
        host._history.Hide();
        host.RequestRender();
    }

    /// <summary>Attaches the session whose notifications this host presents.</summary>
    /// <remarks>
    /// Call on the UI thread. Replacing a session detaches presentation from the old one without dismissing its entries,
    /// disposing it, or cancelling its actions. Presentation starts when the host is loaded and resumes after reloading.
    /// </remarks>
    /// <param name="session">The externally owned session to present.</param>
    /// <exception cref="ArgumentNullException">The session is null.</exception>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The caller is not on the host's UI thread.</exception>
    public void Attach(NotificationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(this._disposed, this);
        if (!this.DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("Attach the notification host on its UI thread.");
        }

        this.Detach();
        this._history.Hide();
        this.ClearHistory();
        this._session = session;
        this.IsExpanded = false;
        this._scrollToFront = false;
        if (this.IsLoaded)
        {
            this.StartPresentation();
        }

        this.RequestRender();
    }

    private void StartPresentation()
    {
        if (this._disposed)
        {
            return;
        }

        if (this._session is not null)
        {
            this._session.Changed -= this.Session_Changed;
            this._session.Changed += this.Session_Changed;
        }

        this._clock.Restart();
        this._timer.Start();
        this.RequestRender();
    }

    private static void Strings_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var host = (NotificationHost)sender;
        host.DismissText = NotificationText.Get(host.Strings, "Dismiss");
        host.DismissAllText = NotificationText.Get(host.Strings, "DismissAll");
        host.ExpandStackText = NotificationText.Get(host.Strings, "ExpandStack");
        host.RequestRender();
    }

    private void Session_Changed(object? sender, EventArgs args)
    {
        this.RequestRender();
    }

    private void RequestRender()
    {
        if (Interlocked.Exchange(ref this._renderQueued, 1) != 0)
        {
            return;
        }

        if (!this.DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref this._renderQueued, 0);
                if (!this._disposed && this.IsLoaded)
                {
                    this.Render();
                    if (this._history.IsOpen)
                    {
                        this.UpdateHistory();
                    }
                }
            }))
        {
            Interlocked.Exchange(ref this._renderQueued, 0);
        }
    }

    private void Render()
    {
        if (this._session is not { } session)
        {
            return;
        }

        var active = session.GetActive();
        this.ActiveCount = active.Length;
        this.CountText = active.Length.ToString();
        if (!this._templateReady)
        {
            return;
        }

        this._stackToggle.IsChecked = this.IsExpanded;
        var many = active.Length > 2;
        var restoreFocus = !many && HasFocus(this._stackToggle);
        if (!many)
        {
            this.IsExpanded = false;
            this._scrollToFront = false;
        }

        var expanded = many && this.IsExpanded;
        var collapsed = many && !expanded;
        this._footer.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        this._hostStack.RowSpacing = many ? 4 : 0;
        var candidates = active.Reverse().OrderByDescending(item =>
            item.Content.Severity is NotificationSeverity.Error or NotificationSeverity.Warning);
        var existing = this._cards.Values.OrderBy(card => this._cardsPanel.Children.IndexOf(card.View))
            .Select(card => active.FirstOrDefault(item => item.Id == card.Id)).OfType<NotificationSnapshot>().ToArray();
        NotificationSnapshot[] visible;
        if (expanded)
        {
            // shortcut: expansion realizes at most 50 cards; virtualize if the session capacity grows.
            var ordered = this.IsTop ? candidates : candidates.Reverse();
            visible = this._scrollToFront ? ordered.ToArray() : existing.Concat(ordered).DistinctBy(item => item.Id).ToArray();
        }
        else
        {
            var held = collapsed && this._isStackHovered
                ? existing
                : existing.Where(item => this._cards[item.Id].IsPointerOver || HasFocus(this._cards[item.Id].View)).ToArray();
            var selection = held.Length > 0 ? held.Concat(existing.Reverse()).Concat(candidates) : candidates;
            var selected = selection.DistinctBy(item => item.Id).Take(collapsed ? 1 : 2);
            visible = (this.IsTop ? selected : selected.Reverse()).ToArray();
        }

        this._hostStack.HorizontalAlignment = this.Position switch
        {
            NotificationPosition.TopLeft or NotificationPosition.BottomLeft => HorizontalAlignment.Left,
            NotificationPosition.TopCenter or NotificationPosition.BottomCenter => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Right
        };
        this._hostStack.VerticalAlignment = this.IsTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        Grid.SetRow(this._stackDeck, this.IsTop ? 1 : 0);
        Grid.SetRow(this._footer, this.IsTop ? 0 : 1);
        this._backplateDirection?.ScaleY = this.IsTop ? -1 : 1;
        this._backplateDirection?.ScaleX = this._hostStack.HorizontalAlignment == HorizontalAlignment.Left ? -1 : 1;
        this._cardsPanel.Margin = new Thickness(8, collapsed && !this.IsTop ? 24 : 8, 8, collapsed && this.IsTop ? 24 : 8);
        this._peekNear?.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        this._peekFar?.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        this._peekExpand?.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        if (!collapsed)
        {
            this._peekHover.Reset();
        }

        this.UpdateStackFan();
        this._stackToggle.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        VisualStateManager.GoToState(this, (this.IsTop ? "Top" : "Bottom") + (expanded ? "Expanded" : "Collapsed"), false);
        AutomationProperties.SetName(
            this._stackToggle,
            NotificationText.Format(this.Strings, expanded ? "CollapseStackFormat" : "ExpandStackFormat", active.Length));
        AutomationProperties.SetHelpText(
            this._stackToggle, NotificationText.Get(this.Strings, expanded ? "ExpandedStackHelp" : "CollapsedStackHelp"));
        var availableHeight = Math.Max(
            0, this.ActualHeight - this.Padding.Top - this.Padding.Bottom - (many ? this._footer.ActualHeight + 4 : 0));
        this._cardsViewport.MaxHeight = expanded ? Math.Min(availableHeight, Math.Min(480, this.ActualHeight * 0.6)) : availableHeight;
        if (!expanded)
        {
            this._cardsViewport.Height = double.NaN;
        }

        this._cardsViewport.VerticalScrollMode = ScrollMode.Enabled;
        this._cardsViewport.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        VisualStateManager.GoToState(this, this.Material.ToString(), false);
        foreach (var id in this._cards.Keys.Except(visible.Select(item => item.Id)).ToArray())
        {
            restoreFocus |= HasFocus(this._cards[id].View);
            this._cardsPanel.Children.Remove(this._cards[id].View);
            this._cards.Remove(id);
        }

        for (var index = 0; index < visible.Length; index++)
        {
            var item = visible[index];
            if (!this._cards.TryGetValue(item.Id, out var card))
            {
                card = this.CreateCard(item);
                this._cards.Add(item.Id, card);
            }

            var currentIndex = this._cardsPanel.Children.IndexOf(card.View);
            if (currentIndex != index)
            {
                if (currentIndex >= 0)
                {
                    this._cardsPanel.Children.RemoveAt(currentIndex);
                }

                this._cardsPanel.Children.Insert(index, card.View);
            }

            card.View.ApplyCardStyle(this.CardStyle);
            if (this.Strings is { } strings)
            {
                card.View.Strings = strings;
            }
            else
            {
                card.View.ClearValue(NotificationCard.StringsProperty);
            }

            card.View.Update(item, this._shadowReceiver, this._iconFactory, announce: !expanded);
        }

        if (restoreFocus)
        {
            if (many)
            {
                this._stackToggle.Focus(FocusState.Programmatic);
            }
            else if (this._returnFocus is { } reference
                     && reference.TryGetTarget(out var target)
                     && target.IsLoaded
                     && target.XamlRoot == this.XamlRoot)
            {
                target.Focus(FocusState.Programmatic);
            }
        }

        if (this._scrollToFront)
        {
            this._scrollToFront = false;
            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (!this._disposed && this._templateReady && this.IsExpanded && ReferenceEquals(this._session, session))
                {
                    this._cardsViewport.UpdateLayout();
                    this._cardsViewport.Height = this._cardsViewport.ActualHeight;
                    this._cardsViewport.ChangeView(null, this.IsTop ? 0 : this._cardsViewport.ScrollableHeight, null, true);
                }
            });
        }
        else if (!expanded && !this._isStackHovered && !this._cards.Values.Any(card => HasFocus(card.View)))
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                if (!this._disposed && this._templateReady && !this.IsExpanded)
                {
                    this._cardsViewport.UpdateLayout();
                    this._cardsViewport.ChangeView(null, this.IsTop ? 0 : this._cardsViewport.ScrollableHeight, null, true);
                }
            });
        }
    }

    private void UpdateStackFan()
    {
        if (!this._templateReady)
        {
            return;
        }

        var motionEnabled = this._uiSettings.AnimationsEnabled;
        var collapsed = this._peekNear?.Visibility == Visibility.Visible;
        var fanned = collapsed && this._windowActive && this._isStackHovered;
        this._peekExpand?.Height = fanned ? 48 : 16;
        this._peekExpand?.VerticalAlignment = this.IsTop ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        this._peekExpand?.Margin = new Thickness(0, fanned && !this.IsTop ? -32 : 0, 0, fanned && this.IsTop ? -32 : 0);
        VisualStateManager.GoToState(this, fanned ? "StackFanned" : collapsed ? "StackRest" : "StackInactive", motionEnabled);
    }

    private void SetExpanded(bool expanded)
    {
        this._peekHover.Reset();
        this.IsExpanded = expanded;
        this._scrollToFront = expanded;
        this.RequestRender();
    }

    private void AnimationsEnabled_Changed(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
    {
        this.DispatcherQueue.TryEnqueue(() =>
        {
            if (!this._disposed && this.IsLoaded)
            {
                this.UpdateStackFan();
            }
        });
    }

    private Card CreateCard(NotificationSnapshot item)
    {
        var card = new Card(item.Id);
        card.View.DismissRequested += () => this._session?.Dismiss(card.Id);
        card.View.PointerEntered += (_, _) => card.IsPointerOver = true;
        card.View.PointerExited += (_, _) =>
        {
            card.IsPointerOver = false;
            this.RequestRender();
        };
        card.View.GotFocus += (_, _) => this.RequestRender();
        card.View.LostFocus += (_, _) => this.RequestRender();
        card.View.ActionRequested += async action => await this.InvokeActionAsync(card.Id, action);
        return card;
    }

    private async Task InvokeActionAsync(Guid id, NotificationAction? expected)
    {
        if (this._session is not { } session || expected is null)
        {
            return;
        }

        try
        {
            await session.InvokeActionAsync(id, expected);
        }
        catch (OperationCanceledException) when (this._disposed)
        {
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            this.ActionFailed?.Invoke(error);
        }
    }

    private void Tick(DispatcherQueueTimer sender, object args)
    {
        if (!this._templateReady)
        {
            this._clock.Restart();
            return;
        }

        var elapsed = this._clock.Elapsed;
        this._clock.Restart();
        if (this._windowActive
            && this._peekExpand?.Visibility == Visibility.Visible
            && this._peekHover.IsRunning
            && this._peekHover.Elapsed >= TimeSpan.FromSeconds(1))
        {
            this.SetExpanded(true);
            return;
        }

        if (!this._windowActive || this.IsExpanded || this._isStackHovered && this._peekNear?.Visibility == Visibility.Visible)
        {
            return;
        }

        var presented = this._cards.Values.Where(card => !card.IsPointerOver && !HasFocus(card.View) && this.IsPresented(card.View))
            .Select(card => card.Id).ToArray();
        this._session?.Advance(elapsed, presented);
    }

    private bool IsPresented(FrameworkElement card)
    {
        if (!card.IsLoaded || card.XamlRoot != this.XamlRoot || this._cardsViewport.XamlRoot != this.XamlRoot)
        {
            return false;
        }

        var bounds = card.TransformToVisual(this._cardsViewport).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        return bounds.Bottom > 0 && bounds.Top < this._cardsViewport.ActualHeight && this._cardsViewport.ActualHeight > 0;
    }

    private static bool HasFocus(FrameworkElement element)
    {
        var focused = element.XamlRoot is null ? null : FocusManager.GetFocusedElement(element.XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, element))
            {
                return true;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private bool Contains(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    private void BuildHistory()
    {
        if (this._session is null)
        {
            return;
        }

        this.ClearHistory();
        if (this.HistoryTemplate?.LoadContent() is not FrameworkElement content
            || content.FindName("PART_HistoryList") is not ListView list)
        {
            return;
        }

        content.DataContext = this;
        content.MaxWidth = Math.Min(content.MaxWidth, Math.Max(0, this.ActualWidth - this.Padding.Left - this.Padding.Right));
        if (content.FindName("PART_HistoryDismissAll") is ButtonBase clear)
        {
            clear.Click += (_, _) =>
            {
                this._session?.DismissAll();
                this._history.Hide();
            };
        }

        this._historyList = list;
        this._historyOmitted = content.FindName("PART_HistoryOmitted") as TextBlock;
        this._history.Content = content;
        this.UpdateHistory();
    }

    private void UpdateHistory()
    {
        if (this._session is not { } session || this._historyList is not { } list)
        {
            return;
        }

        var retained = new HashSet<Guid>();
        foreach (var item in session.GetActive().Reverse())
        {
            retained.Add(item.Id);
            this.UpdateHistoryEntry(item.Id, item.Content, true, item.IsActionRunning);
        }

        foreach (var item in session.GetHistory().Reverse())
        {
            if (retained.Add(item.Id))
            {
                this.UpdateHistoryEntry(item.Id, item.Content, false, false);
            }
        }

        foreach (var id in this._historyRows.Keys.Except(retained).ToArray())
        {
            var entry = this._historyRows[id];
            var restoreFocus = HasFocus(entry.View);
            list.Items.Remove(entry.View);
            this._historyRows.Remove(id);
            if (restoreFocus)
            {
                list.Focus(FocusState.Programmatic);
            }
        }

        AutomationProperties.SetName(list, NotificationText.Format(this.Strings, "HistoryTitleFormat", retained.Count));
        if (this._historyOmitted is not null)
        {
            this._historyOmitted.Text = NotificationText.Format(this.Strings, "HistoryOmittedFormat", session.OmittedCount);
            this._historyOmitted.Visibility = session.OmittedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateHistoryEntry(Guid id, Notification notification, bool active, bool running)
    {
        var list = this._historyList!;
        if (!this._historyRows.TryGetValue(id, out var entry))
        {
            if (this.HistoryItemTemplate?.LoadContent() is not FrameworkElement row
                || row.FindName("PART_HistoryTitle") is not TextBlock title
                || row.FindName("PART_HistoryMessage") is not TextBlock message
                || row.FindName("PART_HistoryDismiss") is not ButtonBase dismiss
                || row.FindName("PART_HistoryActions") is not Panel actions)
            {
                return;
            }

            row.DataContext = this;
            AutomationProperties.SetAutomationId(row, $"HistoryNotification_{id:N}");
            AutomationProperties.SetAutomationId(dismiss, "DismissNotification_" + id.ToString("N"));
            dismiss.Click += (_, _) =>
            {
                this._session?.Dismiss(id);
                this._history.Hide();
            };
            entry = new HistoryEntry(row, title, message, dismiss, actions);
            this._historyRows.Add(id, entry);
            // Keep existing rows in place while reading; new arrivals append until the next opening.
            list.Items.Add(row);
        }

        var restoreFocus = !active && HasFocus(entry.Dismiss);
        entry.Title.Text = NotificationText.Format(
            this.Strings, "HistoryItemFormat", NotificationText.Get(this.Strings, active ? "HistoryActive" : "HistoryClosed"),
            NotificationText.Name(this.Strings, notification));
        entry.Message.Text = notification.Message;
        entry.Dismiss.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        var count = active ? notification.Actions.Count : 0;
        for (var index = 0; index < count; index++)
        {
            if (index == entry.Actions.Children.Count)
            {
                var button = new Button();
                button.Click += async (_, _) =>
                {
                    this._history.Hide();
                    await this.InvokeActionAsync(id, (NotificationAction)button.Tag);
                };
                entry.Actions.Children.Add(button);
            }

            var invoke = (Button)entry.Actions.Children[index];
            restoreFocus |= running && HasFocus(invoke);
            invoke.Content = notification.Actions[index].Label;
            invoke.Tag = notification.Actions[index];
            invoke.IsEnabled = !running;
            AutomationProperties.SetAutomationId(invoke, $"HistoryAction_{id:N}_{index}");
        }

        while (entry.Actions.Children.Count > count)
        {
            var index = entry.Actions.Children.Count - 1;
            restoreFocus |= HasFocus((FrameworkElement)entry.Actions.Children[index]);
            entry.Actions.Children.RemoveAt(index);
        }

        if (restoreFocus)
        {
            var target = active ? entry.Dismiss : list.ContainerFromItem(entry.View) as Control ?? list;
            target.Focus(FocusState.Programmatic);
        }
    }

    private void ClearHistory()
    {
        this._history.Content = null;
        this._historyList = null;
        this._historyOmitted = null;
        this._historyRows.Clear();
    }

    private void Detach()
    {
        this._timer.Stop();
        this._clock.Stop();
        this._peekHover.Reset();
        if (this._session is not null)
        {
            this._session.Changed -= this.Session_Changed;
        }
    }

    private sealed record HistoryEntry(FrameworkElement View, TextBlock Title, TextBlock Message, ButtonBase Dismiss, Panel Actions);

    private sealed class Card
    {
        internal Guid Id { get; }

        internal NotificationCard View { get; } = new();

        internal bool IsPointerOver { get; set; }

        internal Card(Guid id)
        {
            this.Id = id;
        }
    }
}
