// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using ByteEditor = JPSoftworks.Controls.HexEditor;

namespace JPSoftworks.HexEditor.Sample;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToList();
        string? report = null;
        var check = arguments.IndexOf("--self-test");
        if (check >= 0)
        {
            report = arguments[check + 1];
            arguments.RemoveRange(check, 2);
        }

        Stream source = arguments.Count > 0
            ? File.OpenRead(arguments[0])
            : new MemoryStream(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        if (report is not null)
        {
            this.Resources["HexEditorOffsetBrush"] = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue);
        }

        var editor = new ByteEditor { Document = new HexDocument(source), ColumnBandSize = 4 };
        AutomationProperties.SetAutomationId(editor, "SampleHexEditor");
        var host = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition());
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var mode = new ComboBox { ItemsSource = new[] { "Editable", "Fixed length", "Read-only" }, SelectedIndex = 0 };
        AutomationProperties.SetName(mode, "Editing mode");
        mode.SelectionChanged += (_, _) =>
        {
            editor.IsFixedLength = mode.SelectedIndex == 1;
            editor.IsReadOnly = mode.SelectedIndex == 2;
        };
        var bands = new CheckBox { Content = "Column bands", IsChecked = true };
        bands.Checked += (_, _) => editor.ColumnBandSize = 4;
        bands.Unchecked += (_, _) => editor.ColumnBandSize = 0;
        var statusBar = new CheckBox { Content = "Status bar", IsChecked = true };
        statusBar.Checked += (_, _) => editor.ShowStatusBar = true;
        statusBar.Unchecked += (_, _) => editor.ShowStatusBar = false;
        var goTo = new Button { Content = "Go to offset…" };
        goTo.Click += async (_, _) => await editor.GoToOffsetAsync();
        var maxLength = new TextBox { PlaceholderText = "Unlimited", Width = 120, MaxLength = 19 };
        AutomationProperties.SetName(maxLength, "Maximum length in bytes");
        ToolTipService.SetToolTip(maxLength, "Zero or empty allows any length.");
        maxLength.BeforeTextChanging += static (sender, args) =>
            args.Cancel = args.NewText.Length != 0 && !long.TryParse(args.NewText, System.Globalization.NumberStyles.None, null, out _);
        maxLength.TextChanged += (_, _) => editor.MaxLength = maxLength.Text.Length == 0 ? 0 : long.Parse(maxLength.Text);
        options.Children.Add(mode);
        options.Children.Add(bands);
        options.Children.Add(statusBar);
        options.Children.Add(new TextBlock { Text = "Max bytes", VerticalAlignment = VerticalAlignment.Center });
        options.Children.Add(maxLength);
        options.Children.Add(goTo);
        host.Children.Add(options);
        Grid.SetRow(editor, 1);
        var brush = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange);
        if (report is not null)
        {
            var style = new Style { TargetType = typeof(ByteEditor), BasedOn = (Style)this.Resources["DefaultHexEditorStyle"] };
            style.Setters.Add(new Setter(ByteEditor.InactiveSelectionBorderBrushProperty, brush));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 16.0));
            editor.Style = style;
        }

        host.Children.Add(editor);
        this._window = new Window { Title = "Hex editor sample", Content = host };
        this._window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 460));
        this._window.Closed += (_, _) => source.Dispose();
        if (report is not null)
        {
            host.Loaded += async (_, _) =>
            {
                try
                {
                    await Task.Yield();
                    host.UpdateLayout();
                    await CheckEditorAsync(host, editor, brush);
                    await CheckModesAndNavigationAsync(host, editor);
                    await CheckFieldStyleAsync(host, editor);
                    await CheckSharedViewportAsync(host, editor);
                    File.WriteAllText(
                        report,
                        $"PASS. {editor.Document!.Length:N0} bytes. Field and plain styles, spacing, column headings and bands, light and dark themes, mirrored selection, insertion caret, far navigation, editing modes and UI Automation.");
                }
                catch (Exception ex)
                {
                    File.WriteAllText(report, ex.ToString());
                    Environment.ExitCode = 1;
                }

                this._window.Close();
            };
        }

        this._window.Activate();
    }

    private static async Task CheckModesAndNavigationAsync(Grid host, ByteEditor editor)
    {
        var document = editor.Document!;
        editor.IsReadOnly = false;
        editor.IsFixedLength = true;
        var last = document.Length - 1;
        editor.Highlight(last, 1);
        var value = (IValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(editor)!.GetPattern(PatternInterface.Value);
        var revision = document.Revision;
        value.SetValue("3C");
        if (value.IsReadOnly || value.Value != "3C" || document.Length != last + 1 || document.Revision != revision + 1)
        {
            throw new InvalidOperationException("Automation should overwrite the last byte in fixed-length mode.");
        }

        var bands = (Canvas)editor.FindName("ColumnBands");
        var header = (Grid)editor.FindName("ColumnLabels");
        await NextFrameAsync();
        host.UpdateLayout();
        var row = ((StackPanel)editor.FindName("Rows")).Children.OfType<RichTextBlock>().First();
        var digits = last > uint.MaxValue ? 16 : 8;
        var text = ByteEditor.RowText(row).ContentStart;
        var firstBandedDigit = HexEditorRow.HexStart(digits) + 4 * 3;
        var expected = text.GetPositionAtOffset(firstBandedDigit, LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward).X;
        var gap = text.GetPositionAtOffset(firstBandedDigit - 1, LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward).X;
        var label = header.Children.OfType<TextBlock>().Single(b => b.Text == "04");
        var headerX = label.TransformToVisual(row).TransformPoint(default).X;
        var bandStart = bands.Children.Count == 4 ? Canvas.GetLeft((Border)bands.Children[0]) : double.PositiveInfinity;
        if (Math.Abs(headerX - expected) > 1 || Math.Abs(bandStart - (gap + expected) / 2) > 1)
        {
            throw new InvalidOperationException(
                $"Header or bands are misaligned at {last:X}. Byte {expected}, header {headerX} (margin {label.Margin.Left}, width {label.ActualWidth}), band {bandStart}, count {bands.Children.Count}.");
        }

        editor.ColumnBandSize = 1;
        if (bands.Children.Count != 16)
        {
            throw new InvalidOperationException("Single-byte banding should shade eight columns in each pane.");
        }

        var highlights = row.TextHighlighters[ByteEditor.BandHighlight];
        var color = new SolidColorBrush(Microsoft.UI.Colors.Teal);
        editor.ColumnBandForeground = color;
        var rowBytes = Math.Min(HexEditorRow.BytesPerRow, document.Length - (long)row.Tag);
        if (highlights.Foreground != color
            || highlights.Ranges.Count != rowBytes / 2 * 2
            || (rowBytes >= 2
                && (!highlights.Ranges.Any(r => r.StartIndex == HexEditorRow.HexStart(digits) + 3 && r.Length == 2)
                    || !highlights.Ranges.Any(r => r.StartIndex == HexEditorRow.AsciiStart(digits) + 1 && r.Length == 1))))
        {
            throw new InvalidOperationException("The host foreground should tint alternating bytes in both panes.");
        }

        editor.ColumnBandForeground = null;
        if (highlights.Ranges.Count != 0 || bands.Children.Count != 16)
        {
            throw new InvalidOperationException("Background-only bands lost their fill.");
        }

        editor.ClearValue(ByteEditor.ColumnBandForegroundProperty);
        editor.ColumnBandSize = 0;
        if (bands.Children.Count != 0 || highlights.Ranges.Count != 0)
        {
            throw new InvalidOperationException("Disabling banding should remove all bands.");
        }

        editor.OffsetPromptAsync = (current, maximum) => current == last && maximum == last
            ? Task.FromResult<long?>(0)
            : throw new InvalidOperationException("Incorrect offset range.");
        await editor.GoToOffsetAsync();
        if (editor.SelectionStart != 0)
        {
            throw new InvalidOperationException("Host offset prompt did not navigate.");
        }

        editor.OffsetPromptAsync = (_, _) => Task.FromResult<long?>(last);
        await editor.GoToOffsetAsync();
        editor.OffsetPromptAsync = (_, _) => Task.FromResult<long?>(null);
        await editor.GoToOffsetAsync();
        editor.OffsetPromptAsync = (_, _) => Task.FromResult<long?>(-1);
        await editor.GoToOffsetAsync();
        if (editor.SelectionStart != last)
        {
            throw new InvalidOperationException("An invalid or canceled host prompt moved the selection.");
        }

        var delayed = new TaskCompletionSource<long?>();
        editor.OffsetPromptAsync = (_, _) => delayed.Task;
        var pending = editor.GoToOffsetAsync();
        editor.Document = new HexDocument(Stream.Null, isFixedLength: true);
        delayed.SetResult(last);
        await pending;
        if (!value.IsReadOnly || editor.SelectionLength != 0 || editor.Document.Length != 0)
        {
            throw new InvalidOperationException("A stale host prompt or empty fixed-length document exposed an editable byte.");
        }

        editor.Document = document;
        editor.IsFixedLength = false;
        editor.OffsetPromptAsync = (_, maximum) => Task.FromResult<long?>(maximum);
        await editor.GoToOffsetAsync();
        await NextFrameAsync();
        host.UpdateLayout();
        var rows = (StackPanel)editor.FindName("Rows");
        var endRow = rows.Children.OfType<RichTextBlock>().Last();
        var endText = ByteEditor.RowText(endRow).Text;
        var caret = (Border)editor.FindName("EditCaret");
        if (value.Value != ""
            || editor.SelectionLength != 0
            || endText.Length != HexEditorRow.AsciiStart(digits) + document.Length % 16
            || endRow.TextHighlighters[ByteEditor.ActiveHighlight].Ranges.Count != 0
            || ((Canvas)editor.FindName("SelectionOutlines")).Children.Count != 0
            || caret.Visibility != Visibility.Visible
            || caret.Background != editor.Foreground
            || !AutomationProperties.GetItemStatus(editor).Contains("type to append"))
        {
            throw new InvalidOperationException(
                "The insertion position should have a visible caret and status, with no byte or mirrored selection.");
        }

        editor.MaxLength = document.Length;
        if (!value.IsReadOnly || caret.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("An append caret was offered at MaxLength.");
        }

        var beforeAppend = document.Revision;
        try
        {
            value.SetValue("FF");
        }
        catch (InvalidOperationException)
        {
        }

        if (document.Revision != beforeAppend)
        {
            throw new InvalidOperationException("Automation appended past MaxLength.");
        }

        editor.MaxLength = 1;
        editor.Highlight(last, 1);
        if (value.IsReadOnly || document.Length != last + 1)
        {
            throw new InvalidOperationException("Lowering MaxLength changed existing bytes or blocked overwrite.");
        }

        editor.ClearValue(ByteEditor.MaxLengthProperty);
        editor.IsReadOnly = true;
        var readOnlyRevision = document.Revision;
        try
        {
            value.SetValue("FF");
        }
        catch (InvalidOperationException)
        {
        }

        if (!value.IsReadOnly || document.Revision != readOnlyRevision)
        {
            throw new InvalidOperationException("The viewer accepted an automation edit.");
        }

        editor.Document = new HexDocument(Stream.Null);
        editor.IsReadOnly = false;
        await editor.GoToOffsetAsync();
        await NextFrameAsync();
        if (value.IsReadOnly
            || value.Value.Length != 0
            || editor.SelectionLength != 0
            || caret.Visibility != Visibility.Visible
            || rows.Children.Count != 1
            || ((Canvas)editor.FindName("SelectionOutlines")).Children.Count != 0)
        {
            throw new InvalidOperationException("An empty editable document should start with just the insertion caret.");
        }

        editor.Document = document;
    }

    private static async Task CheckSharedViewportAsync(Grid host, ByteEditor editor)
    {
        var original = editor.Document;
        editor.Document = new HexDocument(new MemoryStream(new byte[4096]));
        var viewer = new ByteEditor { Document = editor.Document, IsReadOnly = true, Width = 280, Height = 145 };
        viewer.Highlight(15, 1);
        Grid.SetRow(viewer, 1);
        host.Children.Add(viewer);
        await NextFrameAsync(() => viewer.IsLoaded);
        host.UpdateLayout();
        var viewport = (ScrollViewer)viewer.FindName("Viewport");
        var rows = (StackPanel)viewer.FindName("Rows");
        await NextFrameAsync(() => viewport.HorizontalOffset > 0);
        var farColumn = viewport.HorizontalOffset;
        viewer.Highlight(0, 1);
        await NextFrameAsync(() => viewport.HorizontalOffset < farColumn - 1);
        viewer.Highlight(15, 1);
        await NextFrameAsync(() => viewport.HorizontalOffset >= farColumn - 1);
        var row = (RichTextBlock)rows.Children[0];
        var run = ByteEditor.RowText(row);
        var bounds = run.ContentStart.GetPositionAtOffset(HexEditorRow.HexStart(8) + 45, LogicalDirection.Forward)
            .GetCharacterRect(LogicalDirection.Forward);
        var left = row.TransformToVisual(viewport).TransformBounds(bounds).X;
        if (left < -1 || left + bounds.Width > viewport.ViewportWidth + 1)
        {
            throw new InvalidOperationException("Highlight should reveal the selected byte in a narrow read-only viewer.");
        }

        ((ScrollBar)viewer.FindName("RowScroll")).Value = 30;
        var first = (long)((RichTextBlock)rows.Children[0]).Tag;
        var value = (IValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(editor)!.GetPattern(PatternInterface.Value);
        value.SetValue("A5");
        if (first != 480 || (long)((RichTextBlock)rows.Children[0]).Tag != first)
        {
            throw new InvalidOperationException("A remote overwrite should preserve the viewer's scroll position.");
        }

        host.Children.Remove(viewer);
        editor.Document = original;
    }

    private static async Task CheckEditorAsync(Grid host, ByteEditor editor, Brush overrideBrush)
    {
        var document = editor.Document!;
        if (document.Length == 0)
        {
            throw new InvalidOperationException("The self-test needs at least one source byte.");
        }

        var rows = (StackPanel)editor.FindName("Rows");
        var outlines = (Canvas)editor.FindName("SelectionOutlines");
        var digits = document.Length > uint.MaxValue ? 16 : 8;
        if (document.Length > 16)
        {
            editor.Highlight(14, 3);
            await NextFrameAsync();
            host.UpdateLayout();
            var hex = HexEditorRow.HexStart(digits);
            var ascii = HexEditorRow.AsciiStart(digits);
            if (outlines.Children.Count != 2)
            {
                throw new InvalidOperationException("A selection crossing two rows should have two outlines.");
            }

            var ruler = (TextBlock)editor.FindName("ColumnRuler");
            var cellWidth = ruler.ActualWidth / ruler.Text.Length;
            CheckRowSelection((RichTextBlock)rows.Children[0], (Border)outlines.Children[0], hex + 42, 5, ascii + 14, 2, cellWidth);
            CheckRowSelection((RichTextBlock)rows.Children[1], (Border)outlines.Children[1], hex, 2, ascii, 1, cellWidth);
        }

        var offset = document.Length - 1;
        editor.Highlight(offset, 1);
        host.UpdateLayout();
        var header = (Grid)editor.FindName("Header");
        var status = (TextBlock)editor.FindName("Status");
        var statusBar = (Border)editor.FindName("StatusBar");
        var viewport = (ScrollViewer)editor.FindName("Viewport");
        var heightWithStatus = viewport.ActualHeight;
        if (!editor.ShowStatusBar
            || statusBar.Visibility != Visibility.Visible
            || statusBar.BorderThickness.Top <= 0
            || status.FontFamily.Source == editor.FontFamily.Source)
        {
            throw new InvalidOperationException("The default status should use the UI font.");
        }

        editor.ShowStatusBar = false;
        host.UpdateLayout();
        editor.Highlight(0, 1);
        if (statusBar.Visibility != Visibility.Collapsed
            || viewport.ActualHeight <= heightWithStatus
            || !AutomationProperties.GetItemStatus(editor).Contains("offset 0,")
            || status.Text != AutomationProperties.GetItemStatus(editor))
        {
            throw new InvalidOperationException("Hiding the status should free its space and retain the current automation status.");
        }

        editor.ClearValue(ByteEditor.ShowStatusBarProperty);
        editor.Highlight(offset, 1);
        host.UpdateLayout();
        if (statusBar.Visibility != Visibility.Visible
            || viewport.ActualHeight != heightWithStatus
            || status.Text != AutomationProperties.GetItemStatus(editor))
        {
            throw new InvalidOperationException("Showing the status again should restore the current text and layout.");
        }

        var scroll = (ScrollBar)editor.FindName("RowScroll");
        var pageWithHeaders = scroll.ViewportSize;
        var topWithHeaders = rows.TransformToVisual(editor).TransformPoint(default).Y;
        if (!editor.ShowColumnHeaders || header.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("Column headers should be visible by default.");
        }

        editor.ShowColumnHeaders = false;
        host.UpdateLayout();
        if (header.Visibility != Visibility.Collapsed
            || scroll.ViewportSize <= pageWithHeaders
            || rows.TransformToVisual(editor).TransformPoint(default).Y >= topWithHeaders
            || editor.SelectionStart != offset)
        {
            throw new InvalidOperationException("Hiding the header should reclaim its row and preserve the selection.");
        }

        editor.ClearValue(ByteEditor.ShowColumnHeadersProperty);
        host.UpdateLayout();
        if (header.Visibility != Visibility.Visible || scroll.ViewportSize != pageWithHeaders || editor.SelectionStart != offset)
        {
            throw new InvalidOperationException("Restoring the header should restore the original page size and selection.");
        }

        var farRow = rows.Children.OfType<RichTextBlock>().Single(row => (long)row.Tag == offset - (offset % 16));
        if (!editor.Focus(FocusState.Programmatic))
        {
            throw new InvalidOperationException("The sample could not focus the editor.");
        }

        await NextFrameAsync();
        host.UpdateLayout();
        var caret = (Border)editor.FindName("EditCaret");
        var run = ByteEditor.RowText(farRow);
        var character = HexEditorRow.HexStart(digits) + ((int)(offset % 16) * 3);
        var expected = run.ContentStart.GetPositionAtOffset(character, LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward);
        var actual = caret.TransformToVisual(farRow).TransformPoint(default);
        if (caret.Visibility != Visibility.Visible
            || caret.ActualWidth <= 0
            || caret.ActualHeight <= 0
            || Math.Abs(actual.X - expected.X) > 1
            || Math.Abs(actual.Y - expected.Y) > 1)
        {
            throw new InvalidOperationException(
                $"The edit caret should be at the high digit of byte {offset:X}. Expected {expected}, got {actual}, {caret.Visibility}, focus {editor.FocusState}.");
        }

        var actualBrush = ((SolidColorBrush)outlines.Children.OfType<Border>().Single().BorderBrush).Color;
        var expectedBrush = ((SolidColorBrush)overrideBrush).Color;
        if (editor.FontSize != 16
            || ((SolidColorBrush)farRow.TextHighlighters[ByteEditor.OffsetHighlight].Foreground).Color
            != Microsoft.UI.Colors.CornflowerBlue)
        {
            throw new InvalidOperationException("The host font style or application offset brush was not applied.");
        }

        if (rows.Children.Count > 50 || actualBrush != expectedBrush)
        {
            throw new InvalidOperationException(
                $"Realized {rows.Children.Count} rows. The host selection brush was {expectedBrush}, the row used {actualBrush}.");
        }

        var value = FrameworkElementAutomationPeer.CreatePeerForElement(editor)?.GetPattern(PatternInterface.Value) as IValueProvider;
        var original = new byte[1];
        document.Read(offset, original);
        if (value?.Value != Convert.ToHexString(original) || value.IsReadOnly)
        {
            throw new InvalidOperationException("Automation did not expose the current byte.");
        }

        editor.Width = 160;
        host.UpdateLayout();
        value.SetValue("A5");
        await NextFrameAsync(() =>
        {
            var left = caret.TransformToVisual(viewport).TransformPoint(default).X;
            return left >= -1 && left + caret.ActualWidth <= viewport.ViewportWidth + 1;
        });
        editor.ClearValue(FrameworkElement.WidthProperty);
        editor.IsReadOnly = true;
        if (caret.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("A read-only editor displayed an edit caret.");
        }

        if (!value.IsReadOnly
            || value.Value != "A5"
            || !AutomationProperties.GetItemStatus(editor).Contains("A5", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Automation did not report the edited byte and read-only mode.");
        }

        editor.ClearValue(FrameworkElement.StyleProperty);
        await NextFrameAsync();
        var mirror = outlines.Children.OfType<Border>().Single();
        host.RequestedTheme = ElementTheme.Light;
        await Task.Yield();
        var light = ((SolidColorBrush)editor.Foreground).Color;
        var lightBand = ((SolidColorBrush)editor.ColumnBandForeground!).Color;
        var lightMirror = ((SolidColorBrush)mirror.BorderBrush).Color;
        host.RequestedTheme = ElementTheme.Dark;
        await Task.Yield();
        var darkBand = ((SolidColorBrush)editor.ColumnBandForeground!).Color;
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
            && (light == ((SolidColorBrush)editor.Foreground).Color
                || lightMirror == ((SolidColorBrush)mirror.BorderBrush).Color
                || lightBand == light
                || lightBand == darkBand
                || lightBand.R != lightBand.G
                || lightBand.G != lightBand.B
                || darkBand.R != darkBand.G
                || darkBand.G != darkBand.B))
        {
            throw new InvalidOperationException(
                "The default bands should be neutral in both themes and the selection should follow the host theme.");
        }
    }

    private static async Task CheckFieldStyleAsync(Grid host, ByteEditor editor)
    {
        var root = (FrameworkElement)editor.Content;
        var field = (Border)root.FindName("FieldChrome");
        var states = VisualStateManager.GetVisualStateGroups(root).Single(group => group.Name == "CommonStates");
        var input = ((StackPanel)host.Children[0]).Children.OfType<TextBox>().Single();
        var nativeField = (Border)((FrameworkElement)VisualTreeHelper.GetChild(input, 0)).FindName("BorderElement");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            host.RequestedTheme = theme;
            input.Focus(FocusState.Keyboard);
            await NextFrameAsync(() => nativeField.BorderThickness.Bottom > 1);
            var fill = ((SolidColorBrush)nativeField.Background).Color;
            var stroke = nativeField.BorderThickness;
            editor.Focus(FocusState.Keyboard);
            await NextFrameAsync(() => states.CurrentState?.Name == "Focused");
            if (states.CurrentState?.Name != "Focused"
                || field.BorderThickness != stroke
                || ((SolidColorBrush)field.Background).Color != fill)
            {
                throw new InvalidOperationException(
                    $"Focused {theme} TextBox: {fill}, {stroke}. Hex editor: {((SolidColorBrush)field.Background).Color}, {field.BorderThickness}.");
            }
        }

        host.UpdateLayout();
        var header = (Grid)editor.FindName("Header");
        var labels = (Grid)editor.FindName("ColumnLabels");
        var row = ((StackPanel)editor.FindName("Rows")).Children.OfType<RichTextBlock>().First();
        var statusBar = (Border)editor.FindName("StatusBar");
        if (Math.Abs(header.TransformToVisual(field).TransformPoint(default).Y - editor.BorderThickness.Top) > 1
            || Math.Abs(labels.TransformToVisual(row).TransformPoint(default).X) > 1
            || Math.Abs(labels.TransformToVisual(header).TransformPoint(default).X - editor.Padding.Left) > 1
            || Math.Abs(statusBar.ActualWidth - (field.ActualWidth - editor.BorderThickness.Left - editor.BorderThickness.Right)) > 1)
        {
            throw new InvalidOperationException(
                "The header and status divider should reach the frame, with labels aligned to the inset byte rows.");
        }

        editor.IsEnabled = false;
        await NextFrameAsync(() => states.CurrentState?.Name == "Disabled");
        editor.IsEnabled = true;
        input.Focus(FocusState.Programmatic);
        await NextFrameAsync(() => states.CurrentState?.Name is "Normal" or "PointerOver");

        var ruler = (TextBlock)editor.FindName("ColumnRuler");
        var width = ruler.ActualWidth;
        editor.CharacterSpacing = 0;
        await NextFrameAsync(() => ruler.ActualWidth < width);
        if (row.CharacterSpacing != 0)
        {
            throw new InvalidOperationException("The rows did not adopt the host's compact spacing.");
        }

        editor.ClearValue(Control.CharacterSpacingProperty);
        await NextFrameAsync(() => ruler.ActualWidth == width);

        editor.Style = (Style)Current.Resources["PlainHexEditorStyle"];
        host.UpdateLayout();
        if (!editor.Focus(FocusState.Keyboard)
            || !editor.UseSystemFocusVisuals
            || field.BorderThickness != default
            || states.CurrentState?.Name != "Normal")
        {
            throw new InvalidOperationException("The plain editor should use the system focus visual without the field frame.");
        }
    }

    private static async Task NextFrameAsync(Func<bool>? ready = null)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnRendering(object? sender, object args)
        {
            if (ready?.Invoke() != false)
            {
                frame.TrySetResult();
            }
        }

        CompositionTarget.Rendering += OnRendering;
        try
        {
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private static void CheckRowSelection(
        RichTextBlock row,
        Border outline,
        int hexStart,
        int hexLength,
        int asciiStart,
        int asciiLength,
        double cellWidth)
    {
        var hex = row.TextHighlighters[ByteEditor.ActiveHighlight].Ranges.Single();
        var ascii = row.TextHighlighters[ByteEditor.InactiveHighlight].Ranges.Single();
        if ((hex.StartIndex, hex.Length) != (hexStart, hexLength) || (ascii.StartIndex, ascii.Length) != (asciiStart, asciiLength))
        {
            throw new InvalidOperationException($"The hex and ASCII highlights did not cover the selected bytes in row {row.Tag:X}.");
        }

        var origin = outline.TransformToVisual(row).TransformPoint(default);
        if (outline.Background is not null
            || ((SolidColorBrush)row.TextHighlighters[ByteEditor.InactiveHighlight].Background).Color.A != 0
            || Math.Abs(origin.X - (asciiStart * cellWidth)) > 1
            || Math.Abs(origin.Y) > 1
            || Math.Abs(outline.ActualWidth - (asciiLength * cellWidth)) > 1
            || Math.Abs(outline.ActualHeight - row.ActualHeight) > 1)
        {
            throw new InvalidOperationException(
                $"Row {row.Tag:X}: expected ASCII outline at {asciiStart * cellWidth:F1}, width {asciiLength * cellWidth:F1}. Got {origin}, width {outline.ActualWidth:F1}, height {outline.ActualHeight:F1} of {row.ActualHeight:F1}.");
        }
    }
}
