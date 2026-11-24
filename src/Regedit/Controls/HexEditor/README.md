# WinUI hex editor

`JPSoftworks.Controls.HexEditor` displays and edits a seekable stream through a `HexDocument`. The control, byte document, and theme resources live in this folder. The standalone host in `tests/HexEditor.Sample` compiles these files without referencing the Regedit application.

## Hosting

Merge `Controls/HexEditor/HexEditorResources.xaml` after `XamlControlsResources` in the application's resource dictionary. Give the control a bounded viewport, such as the remaining row of a Grid.

```xml
<hex:HexEditor
    x:Name="Editor"
    xmlns:hex="using:JPSoftworks.Controls"
    AutomationProperties.Name="File contents" />
```

```csharp
// Keep the stream open while its document is in use.
var document = new HexDocument(inputStream);
Editor.Document = document;
Editor.Highlight(0x1_4000_0000L, 16);
```

The caller owns the readable, seekable source and keeps its contents stable. Edits form a draft. Handle `Edited` for change tracking, use `Read(long, Span<byte>)` for a range, and call `CopyTo(destination, cancellationToken)` to stream the result to a separate output. Serialize access to the document and backing stream, including during export.

The viewport uses signed 64-bit byte offsets, an 8 KiB source cache and one visual per visible row. Memory for edits grows with the changed bytes and their ranges. Opening a file, navigating, selecting all, or deleting a large range does not load the entire source. A text clipboard copy accepts up to 1 MiB of selected bytes.

### Editing modes

| Host setting | Behavior |
| --- | --- |
| Default | Overwrite bytes, append at the end, delete, or replace a selection with any number of bytes. |
| `IsFixedLength="True"` | Overwrite existing bytes. Paste starts at the first selected byte and overwrites as many bytes as the clipboard contains. A paste that would run past the end is rejected in full. Delete and Backspace leave the draft intact. |
| `IsReadOnly="True"` | Browse, select, hover, navigate and copy. The edit caret and paste command are hidden, and the UI Automation provider reports a read-only value. |

Choose `new HexDocument(inputStream, isFixedLength: true)` when every view must preserve the source length. A host can also set `IsFixedLength` on an individual editor. A read-only view can share a document with an editable view.

Views share the document's undo history. Ctrl+Z undoes an edit and Ctrl+Y or Ctrl+Shift+Z redoes it. Typing both digits of one byte normally takes one undo step. Undo and redo respect the active view's editing mode and length limit. Each history direction retains up to 128 edits within a 4 MiB budget. Source ranges use offsets, so deleting several GiB of original data can still be undone. Removing more edited data than the budget can retain clears that direction of history.

Set `MaxLength="4096"` to limit user input to 4,096 bytes. This 64-bit property defaults to zero for unlimited input. In a resizable editor, a paste uses the space freed by the selection and inserts as many whole bytes as fit. The status reports a partial paste. Loading a larger document or lowering the limit preserves existing bytes and allows overwrite and deletion. Fixed-length editing and read-only viewing retain their respective restrictions.

## Appearance

The default editor uses the WinUI TextBox background and border for normal, pointer-over, focused and disabled states. Use `Style="{StaticResource PlainHexEditorStyle}"` when the host supplies its own frame. Both styles use the same control and automation peer. The plain style uses the system keyboard focus visual.

The header and status divider span the field. `Padding` sets the inset for their text and the byte rows. Set `CharacterSpacing="0"` for compact data or increase it for more room. The default of `60` adds about 2.5 effective pixels between the starts of successive hex bytes at the default 14-pixel font size. WinUI measures character spacing in thousandths of an em. Header and status text keep their usual spacing.

The list-style header labels the offset, hexadecimal byte positions (`00` through `0F`), and ASCII. Set `ShowColumnHeaders="False"` to give that space to the data rows. Set `ColumnBandSize="4"` to tint alternating groups of four bytes in both panes, or use `1` for alternating individual bytes. Band text uses the theme’s neutral secondary text color. The default group size of `0` turns banding off. Set `ColumnBandForeground` and `ColumnBandBrush` independently in a style or on an instance to customize the text and background. Set either to `{x:Null}` to omit that part of the band.

The input column shows a filled selection. An outline encloses the corresponding bytes in the other column. While the editor has focus, a caret marks the hex digit or ASCII character typing will overwrite. At the end of the data, an insertion caret appears in the active pane and the status reads “type to append” when growth is available. Both panes display only actual bytes. The caret follows the Windows width and blink settings. Read-only views show the selection alone.

Hovering over either representation underlines the same byte in both panes. On the filled selection the underline uses the selection foreground. The tooltip shows the zero-based offset in hexadecimal and decimal. Right-click a byte in either pane and choose **Copy offset** to copy its full hexadecimal address, including the `0x` prefix. **Ctrl+Shift+C** copies the caret's offset.

The default resource dictionary follows light, dark and Windows contrast themes. Applications can override these keys in their own theme dictionaries.

| Area | Resource keys |
| --- | --- |
| Surface and text | `HexEditorBackgroundBrush`, `HexEditorForegroundBrush`, `HexEditorOffsetBrush` |
| Active selection | `HexEditorSelectionBackgroundBrush`, `HexEditorSelectionForegroundBrush` |
| Other column's outline and text | `HexEditorInactiveSelectionBorderBrush`, `HexEditorInactiveSelectionForegroundBrush` |
| Frame | WinUI `TextControlBackground` and `TextControlBorderBrush` resources and their `PointerOver`, `Focused` and `Disabled` variants |
| Plain style keyboard focus | `HexEditorFocusBrush` |
| Header | `HexEditorHeaderBackgroundBrush`, `HexEditorHeaderForegroundBrush`, `HexEditorHeaderDividerBrush` |
| Column bands | `HexEditorColumnBandBrush`, `HexEditorColumnBandForegroundBrush` |
| Byte hover | `HexEditorHoverBackgroundBrush`, `HexEditorHoverStrokeBrush` |
| Metrics | `HexEditorFontFamily`, `HexEditorFontSize`, `HexEditorPadding` |

A host can also set selection brushes per control or in a WinUI style. Set `InactiveSelectionBorderBrush` to customize the outline. Existing `InactiveSelectionBackground` setters supply its fallback color. Use a monospaced font for column alignment. Pair selection foreground and background colors in every supported theme.

```xml
<Style x:Key="FileEditorStyle" BasedOn="{StaticResource DefaultHexEditorStyle}" TargetType="hex:HexEditor">
    <Setter Property="SelectionBackground" Value="{ThemeResource FileSelectionBackgroundBrush}" />
    <Setter Property="SelectionForeground" Value="{ThemeResource FileSelectionForegroundBrush}" />
</Style>
```

## Keyboard and automation

Arrow keys move the caret. Shift extends the selection. Typing edits the byte at the moving end of a selection. Home and End move within a row, Ctrl+Home and Ctrl+End move across the document, and Ctrl+G accepts a hexadecimal offset. F6 switches between hexadecimal and printable ASCII input and preserves the selection. Type at the end to append. Delete removes the highlighted bytes. Ctrl+C copies the active column. Ctrl+V inserts before the current byte or replaces an explicitly selected range.

**Paste overwrite** (Ctrl+Shift+V) writes from the first selected byte and preserves the document length. A shorter paste leaves the rest of the selection intact. A longer paste continues into the following bytes. If the bytes would run past the end, the entire paste is rejected. Both paste commands interpret the clipboard in the active pane. Hosts can invoke either command with `await Editor.PasteAsync()` or `await Editor.PasteAsync(overwrite: true)`.

The editor is one tab stop. Its UI Automation Value pattern reads or sets the byte at the caret as a two-digit hex value. `IsReadOnly` applies to keyboard, clipboard and automation edits. The status bar uses the app's UI font and reports the input pane, offset, byte and selection length. Set `ShowStatusBar="False"` to give its space to the data rows. UI Automation still exposes the item status and announces changes while the editor has focus.

Hosts that accept additional clipboard formats can set `PrepareHexPasteAsync`. It receives the clipboard text for either hex paste command and returns the accepted text, or `null` to cancel. The control applies the result only if the original document and paste target are still current. To decode a custom format, set `ReadCustomHexPaste`. The reader validates the whole string and returns its byte count, or -1 for invalid input. The editor first supplies an empty destination to measure the paste, then a span sized for the bytes that fit.

**Go to offset** opens from the context menu, **Ctrl+G**, or `await Editor.GoToOffsetAsync()`. Enter a hexadecimal address and click **Go** or press Enter. The close button and Escape dismiss the input and restore focus. To use a host dialog, assign a callback that receives the current offset and the inclusive maximum and returns the destination, or `null` to cancel.

```csharp
Editor.OffsetPromptAsync = (current, maximum) => ShowAddressDialogAsync(current, maximum);
```

## Standalone check

Build `tests/HexEditor.Sample/HexEditor.Sample.csproj` for x64 or ARM64. Launch the executable with a file path to open a draft. Switch editing modes or toggle column bands in the sample toolbar. Add `--self-test <report-file>` to check layout, navigation, editing modes, the UI Automation provider and host theming.
