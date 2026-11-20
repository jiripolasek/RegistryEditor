// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

#if REGEDIT_UI_TESTS
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JPSoftworks.Notifications;
using JPSoftworks.Notifications.WinUI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Notifications.Prototype;

internal sealed partial class NotificationPrototypeWindow
{
    private async Task CheckTemplatesAsync()
    {
        var originalTemplate = this._host.Template;
        var originalTemplateValue = this._host.ReadLocalValue(Control.TemplateProperty);
        var originalHostStyle = this._host.Style;
        var originalCardStyle = this._host.CardStyle;
        var originalMaterial = this._host.Material;
        var originalTheme = this.Surface.RequestedTheme;
        var originalActive = this._host.IsWindowActive;
        var originalStrings = this._host.ReadLocalValue(NotificationHost.StringsProperty);
        var originalHistoryTemplate = this._host.ReadLocalValue(NotificationHost.HistoryTemplateProperty);
        var originalHistoryItemTemplate = this._host.ReadLocalValue(NotificationHost.HistoryItemTemplateProperty);
        using var session = new NotificationSession();
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            checks++;
        }

        try
        {
            this._host.Attach(session);
            CheckSubscriptions(this._host, session, Check);
            this._host.IsWindowActive = true;
            this._host.Material = NotificationMaterial.Solid;
            this._host.ClearValue(FrameworkElement.StyleProperty);
            this._host.CardStyle = null;
            this._status.Text = "Checking notification templates...";
            var templates = new ResourceDictionary { Source =
 new Uri("ms-appx:///Notifications/Prototype/NotificationTemplateChecks.xaml") };
            var defaults = new ResourceDictionary { Source =
 new Uri("ms-appx:///Notifications/WinUI/Themes/NotificationControls.xaml") };
            var defaultStyle = (Style)defaults["DefaultNotificationCardStyle"];
            var alternate = (ControlTemplate)templates["AlternateCardTemplate"];
            var customStyle = new Style(typeof(NotificationCard)) { BasedOn = defaultStyle };
            customStyle.Setters.Add(new Setter(Control.TemplateProperty, alternate));
            this._host.Resources[typeof(NotificationCard)] = customStyle;
            var light = new SolidColorBrush(Colors.WhiteSmoke);
            var dark = new SolidColorBrush(Colors.DimGray);
            this._host.Resources.ThemeDictionaries["Light"] = new ResourceDictionary { ["NotificationBackgroundBrush"] =
 light };
            this._host.Resources.ThemeDictionaries["Dark"] = new ResourceDictionary { ["NotificationBackgroundBrush"] =
 dark };
            this.Surface.RequestedTheme = ElementTheme.Light;
            var actionCalls = 0;
            var content = new Notification
            {
                Title = "Template check", Message = "Original message", Key = "template-check", Duration = null,
                IconKey = "save", DismissLabel = "Later",
                Actions = (NotificationAction[])[new NotificationAction("Execute once", _ =>
                {
                    actionCalls++;
                    return Task.CompletedTask;
                }) { IsDefault = true }]
            };
            session.Show(content);
            await Task.Delay(250, this._lifetime.Token);
            var card = TemplateDescendants<NotificationCard>(this._host).Single();
            Check(ReferenceEquals(card.Template, alternate) && card.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue,
                "The host overrode an application-declared implicit card style.");
            Check(ReferenceEquals(card.Background, light), "Local light theme resources did not reach the card.");
            this.Surface.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(150, this._lifetime.Token);
            Check(ReferenceEquals(card.Background, dark), "The existing card did not follow a live dark theme override.");
            Check(card.IconForeground is not null && card.IconSource?.Foreground == card.IconForeground,
                "The custom icon lost its theme foreground.");
            var action = TemplateDescendants<ButtonBase>(card).Single(button => button.Tag is NotificationAction);
            session.Show(content with { Title = "Updated template title", Message = "Updated template message" });
            await Task.Delay(150, this._lifetime.Token);
            Check(TemplateDescendants<TextBlock>(card).Any(text => text.Text == "Updated template title") &&
                  TemplateDescendants<TextBlock>(card).Any(text => text.Text == "Updated template message"),
                "TemplateBinding did not follow keyed replacement.");
            Check(TemplateDescendants<HyperlinkButton>(card).Any(button => Equals(button.Content, "Later")),
                "The alternate template lost the instance dismiss label.");

            this._host.CardStyle = (Style)defaults["NotificationCardNativeStyle"];
            await Task.Delay(250, this._lifetime.Token);
            var native = TemplateDescendants<InfoBar>(card).Single();
            Check(native.Title == "Updated template title" && native.IsIconVisible, "Native template lost title or custom icon bindings.");
            Check(TemplateDescendants<ButtonBase>(card).Any(button => button.Tag is NotificationAction), "Native template did not connect its action presenter.");

            session.DismissAll();
            await Task.Delay(150, this._lifetime.Token);
            this._host.Style = (Style)defaults["NotificationHostNativeStyle"];
            session.Show(content with { Title = "Updated template title", Message = "Updated template message" });
            await Task.Delay(250, this._lifetime.Token);
            card = TemplateDescendants<NotificationCard>(this._host).Single();
            action = TemplateDescendants<ButtonBase>(card).Single(button => button.Tag is NotificationAction);
            var clip =
 TemplateDescendants<Grid>(this._host).Select(grid => grid.Clip).OfType<RectangleGeometry>().Single();
            Check(card.ActualWidth > 0 && card.ActualHeight > 0 && clip.Rect.Width > 0 && clip.Rect.Height > 0 &&
                  TemplateDescendants<ScrollViewer>(this._host).Single().ActualHeight > 0,
                "Changing the empty host style left the next notification without a layout region.");
            session.Show(content with { Severity = NotificationSeverity.Warning });
            await Task.Delay(100, this._lifetime.Token);
            Check(TemplateDescendants<InfoBar>(card).Single().Severity == InfoBarSeverity.Warning,
                "The native theme did not apply the notification severity.");
            session.Show(content with { Title = "Updated template title", Message = "Updated template message" });
            await Task.Delay(100, this._lifetime.Token);
            Check(TemplateDescendants<InfoBar>(card).Single().Severity == InfoBarSeverity.Informational,
                "The native theme retained the previous severity after a neutral update.");

            this._host.Resources.Remove(typeof(NotificationCard));
            this._host.CardStyle = (Style)defaults["NotificationCardFluent2Style"];
            await Task.Delay(150, this._lifetime.Token);
            Check(action.Focus(FocusState.Programmatic), "The action could not receive keyboard focus.");
            this._host.CardStyle = customStyle;
            await Task.Delay(250, this._lifetime.Token);
            Check(ReferenceEquals(action, TemplateDescendants<ButtonBase>(card).Single(button => button.Tag is NotificationAction)) &&
                  ReferenceEquals(FocusManager.GetFocusedElement(card.XamlRoot), action),
                "Changing CardStyle lost the existing action or its keyboard focus.");
            var editor =
 TemplateDescendants<TextBox>(this.Surface).Single(box => AutomationProperties.GetAutomationId(box) == "PrototypeEditor");
            editor.Focus(FocusState.Programmatic);
            this._host.CardStyle = (Style)defaults["NotificationCardFluent2Style"];
            await Task.Delay(250, this._lifetime.Token);
            Check(ReferenceEquals(FocusManager.GetFocusedElement(editor.XamlRoot), editor), "A style change stole focus from the editor.");
            this._host.CardStyle = customStyle;
            await Task.Delay(150, this._lifetime.Token);
            var actionEvents = 0;
            card.ActionRequested += _ => actionEvents++;
            InvokeTemplateButton(action);
            await Task.Delay(150, this._lifetime.Token);
            Check(actionEvents == 1 && actionCalls == 1 && session.GetActive().Length == 0,
                "Changing templates duplicated or disconnected the action handler.");
            session.Show(content with { Actions = [] });
            await Task.Delay(150, this._lifetime.Token);
            card = TemplateDescendants<NotificationCard>(this._host).Single();
            var dismissEvents = 0;
            card.DismissRequested += () => dismissEvents++;
            InvokeTemplateButton(TemplateDescendants<HyperlinkButton>(card).Single());
            await Task.Delay(150, this._lifetime.Token);
            Check(dismissEvents == 1 && session.GetActive().Length == 0, "Custom template dismissal did not run exactly once.");

            await this.CheckScrollFadeAsync(session, defaults, Check);
            for (var index = 0; index < 3; index++)
            {
                session.Show(new Notification { Title = $"Stack template {index}", Duration = null });
            }

            this._host.IsExpanded = true;
            await Task.Delay(200, this._lifetime.Token);
            var identities = session.GetActive().Select(item => item.Id).ToHashSet();
            var views = TemplateDescendants<NotificationCard>(this._host).ToArray();
            var oldViewport = TemplateDescendants<ScrollViewer>(this._host).Single();
            var oldSurface = TemplateDescendants<Grid>(this._host).Single(grid => grid.Name == "PART_ScrollSurface");
            Check(ElementCompositionPreview.GetElementVisual(oldViewport).Opacity == 0 &&
                  ElementCompositionPreview.GetElementChildVisual(oldSurface) is not null,
                "The default host did not redirect its viewport through the scroll mask.");
            this._host.Template = (ControlTemplate)templates["AlternateHostTemplate"];
            this._host.ApplyTemplate();
            await Task.Delay(200, this._lifetime.Token);
            Check(ElementCompositionPreview.GetElementVisual(oldViewport).Opacity == 1 &&
                  ElementCompositionPreview.GetElementChildVisual(oldSurface) is null,
                "Replacing the host template retained its masked visual or hid the original viewport.");
            Check(this._host.IsExpanded && TemplateDescendants<NotificationCard>(this._host).Count() == 3,
                "The host lost expansion state while replacing its template.");
            Check(views.All(view => TemplateDescendants<NotificationCard>(this._host).Contains(view)) &&
                  identities.SetEquals(session.GetActive().Select(item => item.Id)),
                "The host replaced cards or notifications while retemplating.");
            this._host.CardStyle = (Style)defaults["NotificationCardMinimalStyle"];
            await Task.Delay(150, this._lifetime.Token);
            Check(views.All(view => ReferenceEquals(view.Template, defaults["NotificationCardMinimalTemplate"])),
                "The host ignored the consumer-supplied card template.");
            this._host.CardStyle = null;
            this._host.Template = originalTemplate;
            this._host.ApplyTemplate();
            await Task.Delay(150, this._lifetime.Token);
            Check(TemplateDescendants<ToggleButton>(this._host).Single().IsChecked == true &&
                  views.All(view => TemplateDescendants<StackPanel>(view).Any(panel => panel.Name == "DetailsPanel")),
                "Clearing CardStyle did not return to implicit styles while preserving expansion.");
            session.DismissAll();
            this._host.Template = (ControlTemplate)templates["EmptyHostTemplate"];
            this._host.ApplyTemplate();
            session.Show(new Notification { Title = "Hidden template lifetime", Duration =
 TimeSpan.FromMilliseconds(500) });
            await Task.Delay(850, this._lifetime.Token);
            Check(session.GetActive().Length == 1, "An unpresented template consumed notification lifetime.");
            this._host.Template = originalTemplate;
            this._host.ApplyTemplate();
            await Task.Delay(1000, this._lifetime.Token);
            Check(session.GetActive().Length == 0, "Restoring the host template did not resume visible-time expiry.");
            this._host.HistoryTemplate = (DataTemplate)templates["AlternateHistoryTemplate"];
            this._host.HistoryItemTemplate = (DataTemplate)templates["AlternateHistoryItemTemplate"];
            session.Show(content);
            this._host.ShowHistory(this._status);
            await Task.Delay(250, this._lifetime.Token);
            var historyElements = VisualTreeHelper.GetOpenPopupsForXamlRoot(this._host.XamlRoot)
                .SelectMany(popup => TemplateDescendants<FrameworkElement>(popup.Child)).ToArray();
            Check(historyElements.Any(element => Equals(element.Tag, "CustomHistory")) &&
                  historyElements.Any(element => Equals(element.Tag, "CustomHistoryRow")),
                "The host ignored the application history templates.");
            InvokeTemplateButton(historyElements.OfType<HyperlinkButton>().Single(button => button.Name == "PART_HistoryDismissAll"));
            await Task.Delay(150, this._lifetime.Token);
            Check(session.GetActive().Length == 0, "The custom history template lost its dismiss-all handler.");
            await this.CheckLocalizedStringsAsync(session, defaults,
                (ResourceDictionary)templates["TranslatedNotificationStrings"], Check);
            this._status.Text = $"Template checks passed: {checks}";
        }
        catch (Exception error)
        {
            this._status.Text = "Template checks failed: " + error.Message;
        }
        finally
        {
            if (originalStrings == DependencyProperty.UnsetValue) { this._host.ClearValue(NotificationHost.StringsProperty); }
            else { this._host.SetValue(NotificationHost.StringsProperty, originalStrings); }
            if (originalHistoryTemplate == DependencyProperty.UnsetValue) { this._host.ClearValue(NotificationHost.HistoryTemplateProperty); }
            else { this._host.SetValue(NotificationHost.HistoryTemplateProperty, originalHistoryTemplate); }
            if (originalHistoryItemTemplate == DependencyProperty.UnsetValue) { this._host.ClearValue(NotificationHost.HistoryItemTemplateProperty); }
            else { this._host.SetValue(NotificationHost.HistoryItemTemplateProperty, originalHistoryItemTemplate); }
            this._host.Resources.Remove(typeof(NotificationCard));
            this._host.Resources.ThemeDictionaries.Clear();
            this._host.Style = originalHostStyle;
            if (originalTemplateValue == DependencyProperty.UnsetValue) { this._host.ClearValue(Control.TemplateProperty); }
            else { this._host.SetValue(Control.TemplateProperty, originalTemplateValue); }
            this._host.CardStyle = originalCardStyle;
            this._host.Material = originalMaterial;
            this.Surface.RequestedTheme = originalTheme;
            this._host.IsWindowActive = originalActive;
            this._host.Attach(this._session);
        }
    }

    private async Task CheckScrollFadeAsync(NotificationSession session, ResourceDictionary defaults, Action<bool, string> check)
    {
        var position = this._host.Position;
        var style = this._host.Style;
        var cardStyle = this._host.CardStyle;
        try
        {
            session.DismissAll();
            this._host.Style = (Style)defaults["NotificationHostFluent2Style"];
            this._host.CardStyle = (Style)defaults["NotificationCardFluent2Style"];
            this._host.Position = NotificationPosition.TopRight;
            for (var index = 0; index < 12; index++)
            {
                session.Show(new Notification { Title = $"Overflow {index}", Message =
 index % 2 == 0 ? "Details" : string.Empty, Duration = null });
            }

            this._host.IsExpanded = true;
            await Task.Delay(300, this._lifetime.Token);
            var viewport = TemplateDescendants<ScrollViewer>(this._host).Single();
            var mask =
 TemplateDescendants<Microsoft.UI.Xaml.Shapes.Rectangle>(this._host).Single(shape => shape.Name == "PART_ScrollFadeMask");
            var gradient = (LinearGradientBrush)mask.Fill;
            check(viewport.ScrollableHeight > 0 && gradient.GradientStops.First().Color.A == 255 && gradient.GradientStops.Last().Color.A == 0,
                "The top-anchored overflow faded the wrong edge or kept a hard cut.");
            viewport.ChangeView(null, viewport.ScrollableHeight / 2, null, true);
            await Task.Delay(200, this._lifetime.Token);
            check(gradient.GradientStops.First().Color.A == 0 && gradient.GradientStops.Last().Color.A == 0,
                "Scrolling into the middle did not fade both overflow edges.");
            viewport.ChangeView(null, viewport.ScrollableHeight, null, true);
            await Task.Delay(200, this._lifetime.Token);
            check(gradient.GradientStops.First().Color.A == 0 && gradient.GradientStops.Last().Color.A == 255,
                "Reaching the final card left its bottom edge faded.");
            this._host.Position = NotificationPosition.BottomRight;
            await Task.Delay(200, this._lifetime.Token);
            check(viewport.VerticalOffset > 0 && gradient.GradientStops.First().Color.A == 0 && gradient.GradientStops.Last().Color.A == 255,
                "The bottom-anchored overflow faded the wrong edge.");
            session.DismissAll();
            session.Show(new Notification { Title = "No overflow", Duration = null });
            await Task.Delay(200, this._lifetime.Token);
            check(viewport.ScrollableHeight <= 1 && gradient.GradientStops.All(stop => stop.Color.A == 255),
                "Removing overflow left the remaining card faded.");
        }
        finally
        {
            session.DismissAll();
            this._host.Position = position;
            this._host.Style = style;
            this._host.CardStyle = cardStyle;
            this._host.IsExpanded = false;
            await Task.Delay(150, this._lifetime.Token);
        }
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, typeof(NotificationSession))]
    private static int SubscriptionCount(NotificationSession session)
    {
        var handlers = (Delegate?)typeof(NotificationSession)
            .GetField(nameof(NotificationSession.Changed), BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session);
        return handlers?.GetInvocationList().Length ?? 0;
    }

    [DynamicDependency("StartPresentation", typeof(NotificationHost))]
    private static void CheckSubscriptions(NotificationHost host, NotificationSession session, Action<bool, string> check)
    {
        var start =
 typeof(NotificationHost).GetMethod("StartPresentation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        check(SubscriptionCount(session) == 1, "Attaching a loaded host did not subscribe exactly once.");
        start.Invoke(host, null);
        start.Invoke(host, null);
        check(SubscriptionCount(session) == 1, "Repeated loaded notifications duplicated the session subscription.");
        using var other = new NotificationSession();
        host.Attach(other);
        check(SubscriptionCount(session) == 0 && SubscriptionCount(other) == 1,
            "Replacing the session retained its old subscription.");
        start.Invoke(host, null);
        host.Attach(session);
        check(SubscriptionCount(other) == 0 && SubscriptionCount(session) == 1,
            "Reattaching after a loaded notification did not detach cleanly.");
        using var temporary = new NotificationHost();
        temporary.Attach(other);
        start.Invoke(temporary, null);
        start.Invoke(temporary, null);
        temporary.Dispose();
        start.Invoke(temporary, null);
        check(SubscriptionCount(other) == 0, "Disposal or a late loaded notification leaked the session subscription.");
    }

    private async Task CheckLocalizedStringsAsync(NotificationSession session, ResourceDictionary defaults,
        ResourceDictionary translated, Action<bool, string> check)
    {
        session.DismissAll();
        this._host.ClearValue(NotificationHost.HistoryTemplateProperty);
        this._host.ClearValue(NotificationHost.HistoryItemTemplateProperty);
        this._host.CardStyle = (Style)defaults["NotificationCardFluent2Style"];
        this._host.Strings = translated;
        var content = new Notification
        {
            Title = "Localized progress", Message = "File", Key = "localized", Duration = null,
            Progress = new NotificationProgress(0.5),
            Actions = (NotificationAction[])[new NotificationAction("Continue", _ => Task.CompletedTask) { IsDefault =
 true }]
        };
        session.Show(content);
        await Task.Delay(250, this._lifetime.Token);
        var card = TemplateDescendants<NotificationCard>(this._host).Single();
        var action = TemplateDescendants<ButtonBase>(card).Single(button => button.Tag is NotificationAction);
        check(card.DismissLabel == "Schließen" && card.ProgressLabel.EndsWith(" fertig: File", StringComparison.Ordinal),
            "Localized dismissal or reordered progress formatting was ignored.");
        check(AutomationProperties.GetHelpText(action) == "Standardaktion" &&
              TemplateDescendants<ProgressBar>(card).Any(bar => AutomationProperties.GetName(bar) == "Fortschritt") &&
              TemplateDescendants<ProgressRing>(card).Any(ring => AutomationProperties.GetName(ring) == "Vorgang läuft") &&
              TemplateDescendants<ButtonBase>(card).Any(button => AutomationProperties.GetName(button) == "Nachricht schließen"),
            "Localized action, progress, busy or dismissal automation labels were ignored.");
        check(NotificationText.Get(new ResourceDictionary { ["Dismiss"] = 42 }, "Dismiss") == "Dismiss" &&
              NotificationText.Get(translated, "SeverityNeutral") == string.Empty,
            "Missing or non-string resources did not fall back to the English defaults.");

        foreach (var invalid in new[] { "{2}", "{" })
        {
            var broken = new ResourceDictionary
            {
                ["ExpandStackFormat"] = invalid,
                ["ProgressWithMessageFormat"] = invalid,
                ["NotificationNameFormat"] = invalid,
                ["HistoryTitleFormat"] = invalid
            };
            check(NotificationText.Format(broken, "ExpandStackFormat", 3) == "Show all 3 notifications",
                "An invalid translation did not fall back to the built-in count format.");
            this._host.Strings = broken;
            session.Show(content with { Severity = NotificationSeverity.Warning });
            await Task.Delay(150, this._lifetime.Token);
            check(card.ProgressLabel == NotificationText.Format(null, "ProgressWithMessageFormat", "File", 0.5) &&
                  NotificationText.Name(broken, content with { Severity =
 NotificationSeverity.Warning }) == "Warning: Localized progress",
                "An invalid translation broke card progress or its accessible name.");
            this._host.ShowHistory(this._status);
            await Task.Delay(150, this._lifetime.Token);
            check(VisualTreeHelper.GetOpenPopupsForXamlRoot(this._host.XamlRoot)
                .SelectMany(popup => TemplateDescendants<ListView>(popup.Child))
                .Any(list => AutomationProperties.GetName(list).StartsWith("Notification history (", StringComparison.Ordinal)),
                "An invalid translation broke the history surface.");
            this._host.Strings = translated;
        }

        foreach (var (severity, label) in new[]
        {
            (NotificationSeverity.Information, "Hinweis"), (NotificationSeverity.Success, "Erfolg"),
            (NotificationSeverity.Warning, "Warnung"), (NotificationSeverity.Error, "Fehler")
        })
        {
            session.Show(content with { Severity = severity });
            await Task.Delay(100, this._lifetime.Token);
            var peer =
 FrameworkElementAutomationPeer.FromElement(card) ?? FrameworkElementAutomationPeer.CreatePeerForElement(card);
            check(peer.GetName() == $"Localized progress ({label})", "The severity name was not localized: " + severity);
        }

        content = content with { Severity = NotificationSeverity.Neutral, Progress = null, DismissLabel = "Later" };
        session.Show(content);
        await Task.Delay(150, this._lifetime.Token);
        check(card.DismissLabel == "Later" && FrameworkElementAutomationPeer.FromElement(card)!.GetName() == content.Title,
            "Localized defaults replaced an instance dismiss label or exposed the Neutral enum name.");
        content = content with { DismissLabel = null };
        session.Show(content);
        this._host.ShowHistory(this._status);
        await Task.Delay(250, this._lifetime.Token);
        var history = VisualTreeHelper.GetOpenPopupsForXamlRoot(this._host.XamlRoot)
            .SelectMany(popup => TemplateDescendants<FrameworkElement>(popup.Child)).ToArray();
        var clear = history.OfType<Button>().Single(button => button.Name == "PART_HistoryDismissAll");
        var historyDismiss = history.OfType<Button>().First(button => button.Name == "PART_HistoryDismiss");
        check(Equals(clear.Content, "Alle schließen") && Equals(historyDismiss.Content, "Schließen") &&
              AutomationProperties.GetName(history.OfType<ListView>().Single()).StartsWith("Verlauf(", StringComparison.Ordinal) &&
              history.OfType<TextBlock>().Any(text => text.Text == "Aktiv | Localized progress") &&
              history.OfType<TextBlock>().Any(text => text.Text.StartsWith("Beendet | ", StringComparison.Ordinal)),
            "The history templates did not display localized labels, state and severity text.");

        this._host.Strings = new ResourceDictionary
        {
            ["Dismiss"] = "Done", ["DismissAll"] = "Clear now", ["HistoryTitleFormat"] = "History {0}"
        };
        await Task.Delay(200, this._lifetime.Token);
        check(card.DismissLabel == "Done" && Equals(clear.Content, "Clear now") && Equals(historyDismiss.Content, "Done") &&
              ReferenceEquals(action, TemplateDescendants<ButtonBase>(card).Single(button => button.Tag is NotificationAction)),
            "Replacing strings did not refresh existing cards and open history without replacing their controls.");
        InvokeTemplateButton(clear);
        await Task.Delay(150, this._lifetime.Token);
        this._host.Strings = translated;
        for (var index = 0; index < 3; index++)
        {
            session.Show(new Notification { Title = "Localized stack " + index, Duration = null });
        }
        await Task.Delay(200, this._lifetime.Token);
        var toggle = TemplateDescendants<ToggleButton>(this._host).Single();
        check(AutomationProperties.GetName(toggle) == "Alle 3 Nachrichten" &&
              AutomationProperties.GetHelpText(toggle) == "Alle Nachrichten lesen." &&
              TemplateDescendants<Button>(this._host).Any(button => button.Name == "PART_PeekExpand" &&
                  AutomationProperties.GetName(button) == "Nachrichten aufklappen"),
            "The stack ignored its localized count, help or expansion label.");
        this._host.IsExpanded = true;
        await Task.Delay(150, this._lifetime.Token);
        check(AutomationProperties.GetName(toggle) == "3 Nachrichten einklappen" &&
              AutomationProperties.GetHelpText(toggle) == "Nachrichten bleiben beim Lesen sichtbar.",
            "The expanded stack did not use localized text.");
        session.DismissAll();
    }

    private static IEnumerable<T> TemplateDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) { yield return match; }
            foreach (var descendant in TemplateDescendants<T>(child)) { yield return descendant; }
        }
    }

    private static void InvokeTemplateButton(ButtonBase button)
    {
        var peer =
 FrameworkElementAutomationPeer.FromElement(button) ?? FrameworkElementAutomationPeer.CreatePeerForElement(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }
}
#endif
