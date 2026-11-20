// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Notifications;
using JPSoftworks.Notifications.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace JPSoftworks.Regedit.Notifications.Prototype;

internal sealed partial class NotificationPrototypeWindow : Window
{
    private readonly NotificationSession _session;
    private readonly NotificationHost _host = new();

    private readonly ResourceDictionary _notificationThemes = new()
    {
        Source = new Uri("ms-appx:///Notifications/WinUI/Themes/NotificationControls.xaml")
    };

    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _status = new() { Text = "No action invoked.", TextWrapping = TextWrapping.Wrap };
    private NotificationAppearance _appearance = new();

    internal NotificationPrototypeWindow(Action openAnother, Action<Exception> reportFailure)
    {
        this.InitializeComponent();
        this.Title = "Notifications prototype";
        this.ApplyAppearance(this._appearance);
        this._session = new NotificationSession(TimeSpan.FromSeconds(new UISettings().MessageDuration));
        this._host.Attach(this._session);
        this._host.IconFactory = key => key switch
        {
            "save" => new SymbolIconSource { Symbol = Symbol.Save },
            "edit" => new FontIconSource
            {
                Glyph = "\uE70F",
                FontSize = 16,
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"]
            },
            _ => null
        };
        this._host.ActionFailed += error =>
        {
            this._status.Text = "Action failed: " + error.Message;
            reportFailure(error);
        };
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 8 };
        heading.Children.Add(
            new TextBlock
            {
                Text = "Notifications", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"]
            });
        heading.Children.Add(
            new TextBlock
            {
                Text
                    = "Keep typing, selecting and scrolling while messages appear. Each window has its own notification session.",
                TextWrapping = TextWrapping.Wrap
            });
        root.Children.Add(heading);
        var appearance = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 5 };
        Grid.SetRow(appearance, 1);
        root.Children.Add(appearance);

        ComboBox Choice(string label, string id, string[] choices, Action<int> selected)
        {
            var choice = new ComboBox
            {
                Header = label,
                MinWidth = id is "PrototypePosition" or "PrototypeArea" ? 144 : 112,
                Margin = new Thickness(0, 0, 12, 8)
            };
            AutomationProperties.SetAutomationId(choice, id);
            foreach (var item in choices)
            {
                choice.Items.Add(item);
            }

            choice.SelectedIndex = 0;
            choice.SelectionChanged += (_, _) => selected(choice.SelectedIndex);
            appearance.Children.Add(choice);
            return choice;
        }

        Choice(
            "Style", "PrototypeStyle", ["Fluent 2", "Accent rail", "Compact", "Native InfoBar", "Minimal"],
            index => this.ApplyAppearance(this._appearance with { VisualStyle = (NotificationVisualStyle)index }));
        Choice(
            "Material", "PrototypeMaterial", ["Solid", "Acrylic"],
            index => this.ApplyAppearance(this._appearance with { Material = (NotificationMaterial)index }));
        Choice(
            "Actions", "PrototypeActions", ["Links", "Buttons"],
            index => this.ApplyAppearance(this._appearance with { Actions = (NotificationActionPresentation)index }));
        Choice(
            "Dismiss", "PrototypeDismiss", ["Close icon", "Dismiss link"],
            index => this.ApplyAppearance(this._appearance with { Dismiss = (NotificationDismissPresentation)index }));
        Choice(
            "Theme", "PrototypeTheme", ["System", "Light", "Dark"],
            index => this.Surface.RequestedTheme = (ElementTheme)index);
        Choice(
            "Position", "PrototypePosition",
            ["Bottom right", "Bottom center", "Bottom left", "Top right", "Top center", "Top left"],
            index => this._host.Position = (NotificationPosition)index);
        var commands = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 6 };
        Grid.SetRow(commands, 2);
        root.Children.Add(commands);

        void AddButton(string label, string id, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), MinWidth = 112 };
            AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) => action();
            commands.Children.Add(button);
        }

#if REGEDIT_UI_TESTS
        AddButton("Template checks", "PrototypeTemplateChecks", () => _ = this.CheckTemplatesAsync());
#endif
        AddButton(
            "Neutral", "PrototypeNeutral",
            () => this._session.Show(
                new Notification
                {
                    Title = "Workspace updated",
                    Message = "Changes are available from another window.",
                    Severity = NotificationSeverity.Neutral,
                    DismissLabel = "Got it"
                }));
        AddButton("Save icon", "PrototypeSaveIcon", () => this.ShowIconPreview());
        AddButton("Delayed info", "PrototypeInfo", () => _ = this.ShowInformationAsync());
        AddButton("Success", "PrototypeSuccess", () => this.Show("Changes saved", NotificationSeverity.Success));
        AddButton(
            "Warning", "PrototypeWarning", () => this.Show("Connection needs attention", NotificationSeverity.Warning));
        AddButton(
            "Error", "PrototypeError", () => this.Show("Could not complete the operation", NotificationSeverity.Error));
        AddButton("Progress", "PrototypeProgress", () => _ = this.RunProgressAsync());
        AddButton(
            "Action", "PrototypeAction", () => this._session.Show(
                new Notification
                {
                    Title = "Export complete",
                    Message = "Sample.reg",
                    Duration = null,
                    Actions = (NotificationAction[])
                    [
                        new NotificationAction(
                            "Show result", async token =>
                            {
                                await Task.Delay(400, token);
                                this._status.Text = "Result action completed.";
                            })
                    ]
                }));
        AddButton(
            "Failing action", "PrototypeFailingAction", () => this._session.Show(
                new Notification
                {
                    Title = "Result unavailable",
                    Duration = null,
                    Actions = (NotificationAction[])
                    [
                        new NotificationAction(
                            "Try action",
                            _ => Task.FromException(new IOException("The sample destination is unavailable.")))
                    ]
                }));
        AddButton("Message burst", "PrototypeBurst", () => this.PublishBurst());
        AddButton("Default action", "PrototypeDefaultAction", () => this.ShowDefaultActionPreview());
        AddButton("New window", "PrototypeNewWindow", openAnother);
        var workspace = new Grid { ColumnSpacing = 16 };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(workspace, 3);
        AutomationProperties.SetAutomationId(workspace, "PrototypeWorkspace");
        root.Children.Add(workspace);
        var editor = new TextBox
        {
            Header = "Editable content",
            Text
                = "Keep typing here while notifications appear.\r\nThe workspace should keep its size, focus, selection and scroll position.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(editor, "PrototypeEditor");
        workspace.Children.Add(editor);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(list, "PrototypeList");
        Grid.SetColumn(list, 1);
        for (var i = 1; i <= 100; i++)
        {
            list.Items.Add($"Example item {i:000}");
        }

        workspace.Children.Add(list);
        Grid.SetRow(this._status, 4);
        AutomationProperties.SetAutomationId(this._status, "PrototypeActionStatus");
        root.Children.Add(this._status);
        var surface = this.Surface;
        surface.Children.Add(root);
        surface.Children.Add(this._host);
        this._host.ShadowReceiver = root;
        Choice(
            "Area", "PrototypeArea", ["Full window", "Workspace pane"], index =>
            {
                surface.Children.Remove(this._host);
                workspace.Children.Remove(this._host);
                Grid.SetColumn(this._host, index == 0 ? 0 : 1);
                if (index == 0)
                {
                    surface.Children.Add(this._host);
                    this._host.ShadowReceiver = root;
                }
                else
                {
                    workspace.Children.Add(this._host);
                    this._host.ShadowReceiver = list;
                }
            });
        var preview = new Button
        {
            Content = "Preview", Margin = new Thickness(0, 0, 12, 8), VerticalAlignment = VerticalAlignment.Bottom
        };
        AutomationProperties.SetAutomationId(preview, "PrototypePreview");
        preview.Click += (_, _) => this.ShowPreview();
        appearance.Children.Add(preview);
        var dismissAll = new Button
        {
            Content = "Dismiss all",
            Margin = new Thickness(0, 0, 12, 8),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        AutomationProperties.SetAutomationId(dismissAll, "PrototypeDismissAll");
        dismissAll.Click += (_, _) => this._session.DismissAll();
        appearance.Children.Add(dismissAll);
        var history = new Button
        {
            Content = "History", Margin = new Thickness(0, 0, 12, 8), VerticalAlignment = VerticalAlignment.Bottom
        };
        AutomationProperties.SetAutomationId(history, "PrototypeHistory");
        history.Click += (_, _) => this._host.ShowHistory(history);
        appearance.Children.Add(history);
        surface.Loaded += (_, _) =>
        {
            var scale = surface.XamlRoot.RasterizationScale;
            this.AppWindow.Resize(new SizeInt32((int)(1100 * scale), (int)(800 * scale)));
            this.ShowPreview();
        };
        this.Activated += (_, args) =>
            this._host.IsWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        this.Closed += (_, _) =>
        {
            this._lifetime.Cancel();
            this._host.Dispose();
            this._session.Dispose();
            this._lifetime.Dispose();
        };
    }

    private void ApplyAppearance(NotificationAppearance appearance)
    {
        this._appearance = appearance;
        this._host.Style = (Style)this._notificationThemes[$"NotificationHost{appearance.VisualStyle}Style"];
        this._host.Material = appearance.Material;
        var cardStyle = new Style(typeof(NotificationCard))
        {
            BasedOn = (Style)this._notificationThemes[$"NotificationCard{appearance.VisualStyle}Style"]
        };
        cardStyle.Setters.Add(new Setter(NotificationCard.MaterialProperty, appearance.Material));
        cardStyle.Setters.Add(new Setter(NotificationCard.ActionPresentationProperty, appearance.Actions));
        cardStyle.Setters.Add(new Setter(NotificationCard.DismissPresentationProperty, appearance.Dismiss));
        this._host.CardStyle = cardStyle;
    }

    private void ShowDefaultActionPreview()
    {
        this._session.Show(
            new Notification
            {
                Title = "Changes saved",
                Message = "A default action is emphasized and can be invoked with Enter while this toast has focus.",
                Severity = NotificationSeverity.Neutral,
                IconKey = "save",
                Key = "default-action-example",
                Duration = null,
                Actions = (NotificationAction[])
                [
                    new NotificationAction(
                        "Open", _ =>
                        {
                            this._status.Text = "Secondary action invoked.";
                            return Task.CompletedTask;
                        }),
                    new NotificationAction(
                        "Undo", _ =>
                        {
                            this._status.Text = "Default action invoked.";
                            return Task.CompletedTask;
                        }) { IsDefault = true }
                ]
            });
    }

    private void ShowPreview()
    {
        this._session.DismissAll();
        this._session.Show(
            new Notification
            {
                Title = "Export complete",
                Message = "Settings.reg is ready to review.",
                Severity = NotificationSeverity.Success,
                Duration = null,
                Actions = (NotificationAction[])
                [
                    new NotificationAction(
                        "Show file", _ =>
                        {
                            this._status.Text = "Preview action invoked.";
                            return Task.CompletedTask;
                        })
                ]
            });
        this._session.Show(
            new Notification
            {
                Title = "Your workspace changed",
                Message = "Changes from another window are available.",
                Severity = NotificationSeverity.Neutral,
                Duration = null,
                DismissLabel = "Not now",
                Actions = (NotificationAction[])
                [
                    new NotificationAction(
                        "Review changes", _ =>
                        {
                            this._status.Text = "Preview review invoked.";
                            return Task.CompletedTask;
                        })
                ]
            });
    }

    private void ShowIconPreview(string? iconKey = "save")
    {
        var nextIcon = iconKey switch { "save" => "edit", "edit" => null, _ => "save" };
        this._session.Show(
            new Notification
            {
                Title = "Document saved",
                Message = "Your changes have been saved.",
                Severity = NotificationSeverity.Neutral,
                IconKey = iconKey,
                Key = "custom-icon-example",
                Duration = null,
                Actions = (NotificationAction[])
                [
                    new NotificationAction(
                        nextIcon switch { "edit" => "Use edit icon", "save" => "Use save icon", _ => "Hide icon" }, _ =>
                        {
                            this.ShowIconPreview(nextIcon);
                            return Task.CompletedTask;
                        })
                ]
            });
    }

    private void Show(string title, NotificationSeverity severity)
    {
        this._session.Show(
            new Notification
            {
                Title = title,
                Message = "The workspace keeps its position. Repeating this message updates its existing card.",
                Key = severity.ToString(),
                Severity = severity,
                Duration = severity is NotificationSeverity.Warning or NotificationSeverity.Error
                    ? null
                    : TimeSpan.FromSeconds(5)
            });
    }

    private async Task ShowInformationAsync()
    {
        var token = this._lifetime.Token;
        try
        {
            await Task.Delay(600, token).ConfigureAwait(false);
            this.Show("Information", NotificationSeverity.Information);
        }
        catch (Exception error) when (token.IsCancellationRequested &&
                                      error is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void PublishBurst()
    {
        // shortcut: a fixed sample burst exercises bounded storage; real operations should publish one summary.
        for (var i = 1; i <= 75; i++)
        {
            this._session.Show(
                new Notification { Title = $"Background message {i}", Message = "Available in notification history." });
        }
    }

    private async Task RunProgressAsync()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notification = new Notification
        {
            Title = "Processing example items",
            Duration = null,
            Progress = NotificationProgress.Indeterminate,
            Actions = (NotificationAction[])
            [
                new NotificationAction(
                    "Cancel", async token =>
                    {
                        stop.Cancel();
                        await finished.Task.WaitAsync(token);
                    })
            ]
        };
        var handle = this._session.Show(notification);
        try
        {
            for (var i = 1; i <= 10; i++)
            {
                await Task.Delay(400, stop.Token).ConfigureAwait(false);
                handle.Update(
                    notification with { Message = $"{i} of 10 items", Progress = new NotificationProgress(i / 10d) });
            }

            handle.Update(new Notification { Title = "Processing complete", Severity = NotificationSeverity.Success });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            handle.Update(new Notification { Title = "Processing canceled" });
        }
        finally
        {
            finished.TrySetResult();
        }
    }
}
