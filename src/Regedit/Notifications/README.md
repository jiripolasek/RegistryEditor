# In-app notifications

Run a Debug or `RegeditUiTests=true` build with `--notification-prototype`. This opens only the
sample window, before registry activation and protocol registration. No new projects
or dependencies are needed.

The main window and each registry-file window own independent sessions. Routine
operation feedback overlays the content in the bottom-right corner using Fluent 2,
solid material and link actions. Main-window history is an optional command in the
application menu. Success and information last six presented seconds or the Windows
message-duration preference, whichever is longer. Warnings, errors, recovery actions
and active operations remain until dismissed or replaced. Completed exports offer
Open folder, which opens Explorer with the exported file selected. The action captures
that output path and remains available until dismissed.

Exports and snapshots post progress when work starts after file selection. Progress
uses a separate key for each page's export and snapshot, with its own title and Cancel
command. Terminal results replace only their operation's progress. Completing without
a result dismisses unfinished progress; changing a busy flag alone never modifies an older message.
Investigation and file-preview statuses replace earlier notices from the same view.
Browser refresh and navigation retain active operation and recovery-action notifications.
Closing an investigation tab dismisses its active status; switching tabs or docking does not.
Watch action errors replace the same view's previous error and clear when its error state clears.

Field validation, settings/note persistence errors, journal-integrity warnings and
dialog-local results stay with the content they describe. Those contextual InfoBars
are separate from transient operation feedback.

## Boundaries

- `src/Regedit.Core/Notifications`: portable contracts and session state, in the
  `JPSoftworks.Notifications` namespace. BCL dependencies only. No registry, settings,
  app singleton, UI, DI container, or logging dependencies belong here.
- `WinUI`: the renderer, in `JPSoftworks.Notifications.WinUI`. It may reference the
  portable contracts and WinUI/Windows APIs, but no Regedit services or models.
- `Prototype`: isolated playground wiring and sample commands. Production app wiring
  stays outside the portable renderer and core.
- `tests/Regedit.Tests/Notifications`: deterministic core regressions, using the
  existing test executable and project reference.

The portable folders have no Regedit-specific dependencies. App wiring lives in the
window/page classes; `Services/StatusNotifications.cs` maps existing notice kinds and
applies the app lifetime policy. The launch switch and playground remain available
for control development in Debug and UI-test builds. Normal Release builds exclude
the entire `Prototype/` folder, including its code, XAML, and launch entry point.
The portable folders can later move into a library without
changing their namespaces or dependencies.

## Contract

Each window explicitly owns a `NotificationSession` and attaches a `NotificationHost`.
The session implements `INotificationService`; callers receive a `NotificationHandle`.
`Update` replaces the full content, actions and duration. A matching active key also
replaces content. Dismissed handles cannot resurrect messages.

Progress is absent, indeterminate, or a finite fraction from zero to one. Progress
never expires until it is replaced with terminal content. A null duration requires
dismissal. Positive durations measure presented time only, with an optional minimum
readable duration supplied by the platform. The sample respects Windows' message
length preference. Hover, keyboard focus and inactive windows pause expiry.

The host shows up to two cards, then collapses three or more messages into a layered
deck with one front card and a compact count/chevron toggle. Important messages take priority over
routine messages; interaction holds the current cards in place. Overflow is also
available through `NotificationHost.ShowHistory(anchor)`, including actions and dismiss
controls. The consuming app owns that optional command; no persistent history button
is added to the toast area. The playground exposes it in its toolbar. Open history
updates content, action availability and active/closed state in place. Existing rows
keep their positions while reading; new arrivals append until history is reopened.
Both active and closed storage are bounded to 50 items. Under pressure the oldest timed message is
archived first, then the oldest active message. Older closed entries are removed and
counted visibly. Closed history strips action callbacks and remains in memory only.

`Notification.Actions` supplies one or more labelled callbacks. The session takes a
read-only snapshot of the collection. At most one action can have `IsDefault = true`.
That action is emphasized with a semibold link or accent button. Enter invokes it
only when the toast itself has focus; Enter on an action invokes that action, and
typing in the workspace never activates a toast. A default does not make the toast
background clickable. The Default action sample has Open and Undo, with Undo marked
as the default even though it is second.

Actions run once at a time per notification, receive the session cancellation token, and dismiss their
original revision on success. A concurrent replacement survives old action completion.
Failures retain an error message for retry and reach the host's `ActionFailed` event.
Closing a card does not cancel its operation. Closing the session cancels its actions.

## Checks

```powershell
dotnet run --project tests/Regedit.Tests -- --suite notifications
./eng/Test-Sandbox.ps1 -Suite Notifications
./eng/Test-Sandbox.ps1 -Suite NativeNotifications
```

The sample covers severity, keyed replacement, actions, action failure, progress and
cancellation, a bounded message burst, a second independent window, typing, selection,
and scrolling. There are no animation presets, sounds, desktop notifications or disk
history. The focused UI suite covers layout, keyboard and icons. Manual Narrator and High
Contrast checks remain part of release validation.

## Appearance playground

The sample offers Fluent 2, Accent rail, Compact, Minimal, and the original Native InfoBar
presentation. Style, material, link/button actions, icon/link dismissal, and light/dark
theme can be compared independently. Preview shows the same sample content after a
style change. These selections live entirely in the Prototype folder.

Neutral is a severity, not a visual style. It deliberately has no severity icon or
accent rail. Information uses a neutral gray icon; success, warning and error use
semantic theme brushes. Informational icons and indeterminate spinners never use the
user's accent color, which might otherwise suggest success, caution or error.

Minimal keeps the icon or busy indicator, truncated title, actions and dismissal in
one row. It omits the message and detail/progress rows; full content remains in Recent
messages and the accessibility announcement. It works with links or buttons and is
useful for centered positions. The other styles retain their full message layout.

## Host region and position

`NotificationHost` is an overlay control. Place it last in a bounded Grid cell over
the content where notifications should appear, and attach the window's session.
Its `Position` dependency property supports BottomRight (the default), BottomCenter,
BottomLeft, TopRight, TopCenter and TopLeft. Coordinates are relative to the host's
own bounds. Normal width, height, margin and Grid row/column placement limit the area;
the host reports no desired size so notifications do not resize the underlying page.

Cards, backplates and expanded stacks are clipped to that region. Overflow scrolls
inside the available height, including a long individual toast. Fully hidden cards
do not consume their visible lifetime. Top stacks expand down and bottom stacks
expand up; their front message and stack toggle remain at the anchor. Backplate fan
direction follows the edge. Native Recent-message flyouts are standard popups and
can extend outside the host region.

The playground's Position selector exercises all six anchors. Area can constrain the
same host to the workspace's right pane without creating another project or session.

`Notification.IconKey` optionally selects a per-instance custom icon independently
of severity. The WinUI host's `IconFactory` maps application-defined keys to fresh
`IconSource` objects, such as `SymbolIconSource` or `FontIconSource`. It runs on the
UI thread, including when the theme changes; returned sources belong to the renderer.
Null or unresolved keys restore
the severity default, including no icon for neutral notifications. The portable
contract and history retain only the key, never UI objects or icon factories.

Sources without an explicit foreground receive the theme's neutral gray text brush.
This tint follows light, dark and High Contrast themes in both custom and Native
InfoBar presentations. Severity, priority, lifetime and severity-colored rails stay
independent of the custom icon. Indeterminate progress keeps its existing busy
indicator in the custom card styles.

The playground's Save icon button shows a neutral notification using `Symbol.Save`.
Its action switches to a font glyph, hides the icon, and restores the save symbol on
the same notification. This also exercises changing and clearing a live icon source.

`Notification.DismissLabel` provides a per-instance text dismissal such as "Not now"
or "Got it", overriding the host's default dismiss presentation. It only dismisses
that notification and does not invoke its primary action or run a callback. Other
notifications keep their own labels and the host defaults.

Middle-clicking anywhere on a notification card dismisses that card, including over
its actions or dismissal controls. This applies to all templates, does not invoke an
action, and does not cancel a running operation.

Acrylic uses the platform `AcrylicInAppFillColorDefaultBrush`, including Windows'
solid fallback for disabled transparency or High Contrast. This blurs app content
behind the card. It does not change the window's desktop backdrop.

References: [Fluent 2 toast](https://fluent2.microsoft.design/components/web/react/core/toast/usage)
and [WinUI in-app acrylic](https://learn.microsoft.com/en-us/windows/apps/develop/ui/in-app-acrylic).

## Collapsed stacks

Three or more active notifications collapse automatically. Two decorative backplates
indicate the queue; only the front notification is presented and consumes visible
lifetime. A compact count/chevron expands the active cards in a bounded scrolling
area, without moving the workspace. Toggling it again returns to the front card. Keyboard and pointer users
use the same toggle. Clicking a backplate also expands immediately. Hovering over
the backplates for one second expands without moving keyboard focus; leaving early
cancels the dwell. Pointer exit never collapses an expanded stack.

Hovering fans the two backplates outward with a short eased transition. The front
card and its controls stay fixed throughout the preview. Leaving the
deck folds the preview back. Hovering the deck pauses expiry and holds the front
message in place. The hover preview remains visible when Windows animations are
off, switching poses immediately. Changes to the Windows setting apply while the
window is open. Backplate click and dwell expansion remain available with animations
disabled. The front card only previews the fan-out when hovered; it never triggers
dwell expansion.

Expiry is paused while reviewing the expanded stack. The review viewport keeps its
height so new arrivals append without moving existing cards or resetting the reader's
scroll position. Dismissing down to two messages automatically returns to the ordinary
layout. Expanded cards retain
their actions and instance-specific dismiss labels. Native and custom presentations
share host-controlled announcements so expansion does not announce the entire queue.

The current bounded prototype realizes at most 50 expanded cards. Virtualization is
appropriate if the session capacity grows. Stack styling follows the selected theme,
material and card corners; backplates carry no status color or interactive content.


## Templates and application themes

The portable controls and default theme are separate:

- `WinUI/NotificationCard.cs` exposes notification data, actions and dismissal.
- `WinUI/NotificationHost.cs` owns presentation, placement and the stack lifecycle.
- `WinUI/Themes/` contains every bundled template, brush, glyph and preset style,
  including the adapter for the optional native InfoBar presentation.
- The app's `Themes/Generic.xaml` merges `NotificationControls.xaml` as its default
  theme. The controls themselves never load a dictionary or look up a preset name.
- `Prototype/` selects the bundled styles for the playground. Its appearance enum
  and record are application configuration, outside the portable controls.

Both controls derive from `Control` and support ordinary implicit styles, explicit
styles and replacement `ControlTemplate` definitions. `NotificationHost.CardStyle`
is optional. When it is null, generated cards use the application's implicit style;
the host does not assign a local default style. Clearing an explicit `CardStyle`
returns to normal style lookup.

The `PART_` prefix identifies elements the control finds by name. Preserve the
names and types documented below for the behaviors your replacement template
supports. Decorative element names and visual-state names do not need this prefix.

For example, an app can declare this implicit card style in its page or application
resources, without changing the host or the notification requests:

```xml
<Style TargetType="notifications:NotificationCard">
    <Setter Property="Padding" Value="16,8" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="notifications:NotificationCard">
                <Border Padding="{TemplateBinding Padding}"
                        Background="{TemplateBinding Background}">
                    <StackPanel Spacing="8">
                        <TextBlock Text="{TemplateBinding Title}" />
                        <ContentPresenter x:Name="PART_ActionsPresenter" />
                        <HyperlinkButton x:Name="PART_DismissAction"
                                         Content="{TemplateBinding DismissLabel}" />
                    </StackPanel>
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

The template can bind to projected data such as `Title`, `Message`, `Severity`,
`IconSource`, `DismissLabel`, `ProgressLabel`, `ProgressValue` and content visibility
properties. Optional parts receive stable action buttons (`PART_ActionsPresenter`), a
custom icon (`PART_CustomIcon`), dismissal (`PART_CloseButton` and
`PART_DismissAction`), a shadow (`PART_Surface`), or native InfoBar closure
(`PART_NativeNotice`). Text and progress controls are ordinary bindings; the component
does not depend on their names or layout. The `Materials` and `Severities` visual-state
groups are optional theme contracts. Action panel, link, button and emphasized-action
styles are properties on the card. Default-action keyboard handling and announcements
remain in the control.

Merge `Notifications/WinUI/Themes/NotificationControls.xaml` explicitly when using
named styles. `DefaultNotificationCardStyle` supplies the default presentation;
`NotificationCardFluent2Style`, `NotificationCardAccentRailStyle`,
`NotificationCardCompactStyle`, `NotificationCardMinimalStyle` and
`NotificationCardNativeStyle` supply the bundled choices. Corresponding
`NotificationHost*Style` resources select matching card styles, spacing and corners.
An app can base its styles on any of these, or provide a complete replacement.

`Material`, `ActionPresentation` and `DismissPresentation` are card styling
properties. The host's separate `Material` property controls its backplates. There
is no preset-selection policy in either component. The playground applies these
properties through styles to preserve its existing controls and examples.

`NotificationResources.xaml` defines semantic brushes, glyphs, padding, corner radii,
spacing, card width and action styles. Brush defaults redirect to platform resources
in explicit Light, Dark and HighContrast dictionaries. App, page or host resource
overrides update existing controls. Neutral and custom icons use gray rather than
the user accent; contrast themes use system foreground/background pairs and a solid
acrylic fallback. Default glyphs are theme resources, so they can also be replaced.

The default host fades only scroll edges with hidden content, revealing the page
behind the stack. The mask gradient belongs to the template; the optional paired
parts `PART_ScrollSurface` (`Grid`) and `PART_ScrollFadeMask` (`FrameworkElement`)
enable composition masking of `PART_CardsViewport`. Place the viewport and mask as
children of that surface, with matching bounds. Omitting these parts leaves normal
scrolling. The `ScrollTopClear`/`ScrollTopOverflow` and
`ScrollBottomClear`/`ScrollBottomOverflow` states select the mask edges.
`NotificationOverflowFadeColor` is transparent in Light/Dark and opaque in HighContrast.
Template replacement and disposal restore source visuals and release the mask.

Host templates require `PART_HostStack`, `PART_StackDeck`, and `PART_Footer` (`Grid`),
`PART_CardsPanel` (`StackPanel`), `PART_CardsViewport` (`ScrollViewer`), and
`PART_StackToggle` (`ToggleButton`). `ActiveCount`, `CountText`, and `IsExpanded` expose
state for custom chrome. `PART_HostClip` (`RectangleGeometry`) optionally clips the
region. `PART_BackplateDirection` (`ScaleTransform`), `PART_PeekNear` and `PART_PeekFar`
(`Border`), and `PART_PeekExpand` (`Button`) provide optional backplates and hover
expansion. Preserve their named parts and state groups from the default theme when
retaining those interactions. Missing required parts suspend presentation and
visible-time expiry until a usable template is applied. Sessions, card identity and
expansion state survive template replacement.

History is also theme-owned. `HistoryTemplate` supplies a root with a `PART_HistoryList`
(`ListView`), plus optional `PART_HistoryDismissAll` (`ButtonBase`) and
`PART_HistoryOmitted` (`TextBlock`) parts. `HistoryItemTemplate` supplies
`PART_HistoryTitle` and `PART_HistoryMessage` (`TextBlock`), `PART_HistoryDismiss`
(`ButtonBase`), and `PART_HistoryActions` (`Panel`). An app can replace both templates,
including their layouts and implicit button styles. Existing rows and action buttons
remain stable during live updates. The bundled templates are
`DefaultNotificationHistoryTemplate` and `DefaultNotificationHistoryItemTemplate`.
`ShadowDepth` is likewise a card style property rather than a fixed visual value in the
component.

## Localization and event threading

`NotificationHost.Strings` accepts a `ResourceDictionary` containing notification-owned
UI text and automation labels. It is forwarded to generated cards. Standalone card styles
can set `NotificationCard.Strings`; an explicit host dictionary takes precedence.
Missing or non-string values use English defaults. Neutral notifications have no severity
prefix by default. Severity display text is separate from enum names.

For example, declare this dictionary in page or application resources and assign it through
a host style, or directly with `Strings="{StaticResource NotificationTranslations}"`:

```xml
<ResourceDictionary x:Key="NotificationTranslations">
    <x:String x:Key="Dismiss">Schließen</x:String>
    <x:String x:Key="SeverityWarning">Warnung</x:String>
    <x:String x:Key="ExpandStackFormat">Alle {0} Nachrichten anzeigen</x:String>
</ResourceDictionary>
```

An app can populate the dictionary from its own `.resw` files or localization service.
Assign a new dictionary on the UI thread to switch languages and refresh existing cards
and open history. Mutating an assigned dictionary does not itself trigger a refresh.
Instance-specific dismiss labels and application-supplied notification/action text retain
their own translations. Native WinUI controls retain the platform's localization.
History templates receive the host as their data context; `DismissText` and
`DismissAllText` expose localized labels to compiled bindings. The default history dictionary has
a small theme-owned code-behind class to support these bindings.

The supported keys and English defaults are:

| Key | Default |
|---|---|
| `Dismiss` | `Dismiss` |
| `DismissNotification` | `Dismiss notification` |
| `DefaultAction` | `Default action` |
| `Progress` | `Operation progress` |
| `Busy` | `Operation in progress` |
| `ExpandStack` | `Expand notification stack` |
| `ExpandStackFormat` | `Show all {0} notifications` |
| `CollapseStackFormat` | `Collapse {0} notifications` |
| `ExpandedStackHelp` | `Notifications won't close automatically while you review them.` |
| `CollapsedStackHelp` | `Expand the stack to review all active notifications.` |
| `DismissAll` | `Dismiss all active notifications` |
| `HistoryTitleFormat` | `Notification history ({0})` |
| `HistoryOmittedFormat` | `Older notifications removed: {0}.` |
| `HistoryActive` | `Active` |
| `HistoryClosed` | `Closed` |
| `HistoryItemFormat` | `{0} · {1}` |
| `NotificationNameFormat` | `{0}: {1}` |
| `ProgressFormat` | `{0:P0} complete` |
| `ProgressWithMessageFormat` | `{0} · {1:P0} complete` |
| `SeverityNeutral` | Empty (no severity prefix) |
| `SeverityInformation` | `Information` |
| `SeveritySuccess` | `Success` |
| `SeverityWarning` | `Warning` |
| `SeverityError` | `Error` |

`NotificationNameFormat` receives severity text and title. `HistoryItemFormat` receives
active/closed text and the formatted notification name. Count formats receive the count.
`ProgressFormat` receives the fraction; `ProgressWithMessageFormat` receives the message
and fraction. Formats use `CultureInfo.CurrentCulture`, so translations may reorder arguments.
Malformed translated formats fall back to the built-in English format for that key.

`NotificationSession.Changed` is synchronous on the thread performing each change, outside
the session lock. Action completion may raise it on a background thread, and concurrent
publishers may raise it concurrently. The WinUI host marshals these notifications through
its dispatcher; other subscribers must marshal before touching UI. Action callbacks begin
on their invocation thread and control their own asynchronous work. The WinUI card's input
events originate on the UI thread; `ActionFailed` follows the awaiting caller's continuation
context. Closing a toast, replacement, and eviction do not cancel a running action. Only
session disposal cancels the supplied session token.

## Sandbox UI checks

Notification UI tests run through the existing Sandbox harness, with a disposable
standard account and application profile:

```powershell
./eng/Test-Sandbox.ps1 -Suite Notifications
./eng/Test-Sandbox.ps1 -Suite NativeNotifications
./eng/Test-Sandbox.ps1 -Suite Notifications -TestFilter Templates
```

The suites check implicit application styles, explicit card styles, theme overrides,
template replacement, keyed updates, action/dismissal handlers, keyboard behavior,
placement, icons, material choices, stacking and live history. The Native AOT suite
also rejects a process that loads CoreCLR. Manual Narrator and High Contrast checks
remain part of release validation. Builds run on the host; launches and input run
inside Sandbox.
