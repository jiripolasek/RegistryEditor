# User guide

Modern Registry Editor is a native WinUI 3 app with an Explorer-inspired layout: tabs, navigation history, a breadcrumb omnibox, a resizable registry tree, and a sortable value list. Built with C# / .NET 11 and Windows App SDK 2.6.5.

For source builds and prerequisites, see [Development](../dev/readme.md). Commands below run from the repository root or the directory containing `regedit2.exe`, as shown.

## Open a registry path or link

Pass one registry path to `regedit2.exe`. Hive aliases, full hive names, the `Computer\` prefix, and remote paths are accepted. Quote paths containing spaces; add `--32` before or after the path to use the 32-bit registry view (the default is 64-bit).

```powershell
.\regedit2.exe 'HKCU\Software\Microsoft'
.\regedit2.exe 'Computer\HKEY_LOCAL_MACHINE\SOFTWARE' --32
.\regedit2.exe '\\SERVER\HKLM\Software'
```

Each launch opens a new window at the requested location. Without arguments, the app restores saved tabs. Invalid arguments show a startup notice; missing or inaccessible keys show the existing navigation error.

### Taskbar Jump List

Right-click Registry Editor's taskbar icon for **New window**, **New administrator window**, and saved **Favorites**. New windows start at Computer; the administrator task requests Windows elevation. Favorites retain their registry view, including remote locations. Windows controls the number of visible entries and whether custom groups are allowed by privacy settings.

The list refreshes at startup and when favorites change. Removing an entry from the Jump List keeps the favorite inside Registry Editor and remembers that it should stay hidden. Delete and add that favorite again to include it again.

Remote registry connections are a preview. The MVP release checks cover local registries. Live-server checks for access failures, disconnects and Journal Revert are deferred.

The address bar, launch arguments, and favorite locations also accept PowerShell drive paths (`HKLM:\Software`), provider paths (`Registry::HKEY_LOCAL_MACHINE\Software`, including the `Microsoft.PowerShell.Core\` prefix), and registry file headers (`[HKEY_LOCAL_MACHINE\Software]`). Matching single or double quotes around a pasted path are accepted. These inputs navigate to a key and retain the selected registry view.

**Settings > Registry view > Always show address as text** replaces breadcrumbs with the full registry path in every registry tab. Click the address or press Ctrl+L to edit it. The preference is remembered; turn it off to restore breadcrumbs.

Hover over a favorite in the address suggestions or select it with the arrow keys to reveal **More actions**. Choose **Show in Favorites organizer** to select the bookmark in its folder, or **Remove favorite…** to remove it after confirmation. You can also open the menu with Shift+F10 on a selected favorite or by pressing and holding the row on a touchscreen.

Committing an address successfully moves keyboard focus to the registry list, ready for arrow-key navigation. Invalid or inaccessible addresses keep focus in the address editor so you can correct the draft.

Spaces at the end of a key name are significant, including in unquoted paths. A whitespace-only key name is also valid. Navigation, copied paths, favorites, and restored tabs preserve these names exactly. Padding outside explicit quotes or a registry file header is ignored.

**Copy path as…** is available in the tree and row context menus, the empty-list menu, and the list's **See more** menu. Choose **PowerShell drive path**, **PowerShell provider path**, or **Registry file header**. The drive format is available for local HKCU and HKLM keys; the provider and header formats support all local hives. Unsupported formats are disabled for Computer and remote locations. **Copy key path** and Ctrl+Shift+C still copy the standard address, including the server name for remote keys. Copied paths do not encode the registry view; choose the corresponding 32-bit or 64-bit view in the receiving tool. PowerShell paths are copied as literal path text, without command or string escaping.

**Go to HKCU / Go to HKLM** in a local key's tree or row context menu opens the same subkey path in the other hive. The empty-list context menu switches the current key, as does **Ctrl+Shift+H**. Switching keeps the selected 32-bit or 64-bit registry view and adds to the tab's navigation history. If the destination is missing or inaccessible, the current key stays open and an error is shown. Switching is unavailable for other hives and remote locations. The row menu hides the action for values and multiple selected rows.

**Search the web** in a key or value's context menu opens its path in your default browser. A value search includes its name, with `(Default)` for unnamed values; value data is never included. The tree and empty-list context menus also offer web search for the selected or current key. Computer and multiple selections cannot be searched. Computer names are omitted, and user-SID segments beginning with `S-1-5-21-` directly under HKEY_USERS are replaced with `<SID>`. Well-known user hives such as `S-1-5-18` and `.DEFAULT` remain useful search terms.

**Settings > Web search** configures the search engine. Bing is the default; **Use preset** also offers Google and DuckDuckGo. Expand the setting to enter a custom HTTP or HTTPS search URL containing `{query}`, which is replaced with the URL-encoded path. Press Enter or leave the field to save. Invalid URLs or failed saves restore the previous preference.

Starting Registry Editor registers the `regedit2://` URI scheme for the current Windows user, without requiring administrator rights. Run the executable once from the location where you intend to keep it before opening links from Run, a browser, or another app.

```powershell
Start-Process 'regedit2://HKCU/Software/Microsoft'
Start-Process 'regedit2://HKEY_LOCAL_MACHINE/SOFTWARE?view=32'
.\regedit2.exe 'regedit2://HKCU/Software/My%20App'
```

Links support local hives and `regedit2://Computer`. Separate keys with `/` (backslashes are also accepted). Percent-encode special characters in each key name, such as `%20` for a space, `%23` for `#`, `%3F` for `?`, and `%25` for `%`; `%2F` is a literal slash within a key name. Optional `?view=32` or `?view=64` selects the registry view and takes precedence over `--32`. Links only navigate; they do not edit the target key or its values. Remote connections continue to use command-line paths.

## Features

| Area | Implementation |
| --- | --- |
| Navigation | Reorderable tabs with context menus; independent back/forward/up history; clickable breadcrumbs with overflow; editable omnibox and hive menu; HKCU-style aliases; address suggestions; local and remote paths; session restoration |
| Browsing | All five standard hives; lazy registry tree; selectable, reorderable columns and fixed key/value grouping; type-to-filter; remembered values-only/combined list and optional value preview |
| Editing | Create, rename, copy, and delete keys and values; multi-selection deletion; default values; string, expandable string, multi-string, DWORD, QWORD, and binary editors |
| Raw data | Edit any registry type directly in the hex or ASCII pane, including uncommon or malformed values. F6 switches panes, Ctrl+G goes to an offset, and Ctrl+End moves to the end to append. Ctrl+V inserts at the caret or replaces a selection. **Paste overwrite** (Ctrl+Shift+V) preserves the value's length. Ctrl+Z undoes draft edits and Ctrl+Y redoes them. |
| Search | Find/Find Next and retained find-all tabs; matched text and paths; value-type filters; named saved searches; cancellation and inaccessible-key count |
| Investigations | Branch snapshots and selected difference export; persistent change journal and conflict-checked revert; local live watch; numeric and binary value inspector; key notes and named workspaces; copy selected changes as PowerShell or reg.exe scripts |
| Favorites | Shared searchable panel and full tab; persistent folder tree and bookmark list; explicit Add favorite dialog and registry address star; rename, move, remove, reorder, and open in a new tab; computer and registry view preserved |
| Files | Unicode `.reg` export; import through Windows `reg.exe`; whole-registry export; native hive save/load/unload |
| Administration | Native Windows permissions dialog with advanced ACL/ownership/auditing pages; remote HKLM/HKU connections; 32/64-bit views; explicit administrator launch |
| Presentation | Mica Alt behind the tabs; tabs in the title bar with an administrator shield when elevated; global application menu; compact breadcrumb and command rows; theme-aware content surface; native WinUI toolbar, menus, and dialogs; system/light/dark appearance; font selection; status-bar toggle; compact layout at narrower sizes; printing of a key, branch, or entire registry |

Tabs occupy the title bar, with the app icon before the first tab and an administrator shield when elevated. Click the app icon to open the Windows system menu. The application menu stays at the top right beside the caption buttons, available on every page. The registry navigation row contains the omnibox, an optional separate search box, and **Tools**. The menu groups tab commands, tool pages/workspaces, registry files/connections, and settings/help. The application menu's **Tools** submenu and the toolbar's **Tools** menu share the same commands: Favorites and Notes open in the right sidebar, Journal and Watches in the bottom panel, and Saved searches and Workspaces in tabs. Their independent **Right sidebar** and **Bottom panel** checkmarks show which panels are visible. **Network registry** groups connection and disconnection. Empty space beside the tabs remains draggable, and Windows supplies the caption buttons.

Right-click a main tab, or focus it and press **Shift+F10**, to close it, close the other main tabs, or close tabs to its right. Browser tabs also offer **Duplicate tab**, which opens the same location and registry view beside the source. Search, Journal, and Watches tabs offer **Move to bottom panel**. Tab actions apply to the tab whose menu you open; bulk close leaves bottom-panel tabs and unsaved note drafts intact.

**Reopen closed tab** in the application menu or a tab context menu, or Ctrl+Shift+T, restores the most recently closed registry tab. The current window remembers up to 20 closed registry tabs, including their computer, registry view, column/tree layout, selected value and tab position. This history lasts for the window session; search and tool tabs are excluded.

**Settings** opens a reusable tab with Community Toolkit settings cards and expanders. **Appearance** contains the app theme and **Window backdrop**, which switches the current window between Mica (the default, using Mica Alt) and Acrylic immediately. Windows may replace the material with a solid color when transparency effects are unavailable or disabled. **Registry text** contains **Data column font**, which controls the family and size for value contents. The tree and other columns use the standard UI font. Turning off the Data override restores that same font. **Registry view** contains status bar, list toolbar, keys, parent row and value preview. The form is left aligned, grows to 960 effective pixels, and stacks its controls when narrow. Changes save automatically and apply to every registry tab; validation or save failures restore the previous preference and display an error. The list's **View** menu keeps its quick toggles, and Settings reflects those changes when revisited. The parent-row preference stays remembered while keys are hidden.

Enter a query in the separate search box and press Enter or its search icon to search the current key and subkeys. The adjacent **Advanced search** action opens the options dialog with your query. Type `?` in the omnibox to access the same search actions immediately. Both fields share recent queries. Turn off **Separate search box** under **Settings > Appearance** to give its space to the omnibox. Ctrl+F then opens the omnibox in Search mode. Type `>` in the omnibox to open the command palette immediately, or `*` to enter Favorites mode. The toolbar fits on one row when space narrows or a panel is pinned. The **Tools** menu opens downward and aligns with the right edge of its button.

**My activity** in Advanced search finds current values that were **Edited** in Regedit, **Inspected** explicitly, or **Either**. Turn on inspection recording under **Tools → Recently inspected**. Leave **Find what** blank to search by activity alone.

**Key last modified** accepts **Last hour**, **Last 24 hours**, **Last 7 days**, **Last 30 days**, or a custom range. A date-only search shows keys by default. Add text, value types, or an activity filter to narrow the results. Value matches use their containing key’s timestamp.

**Typed query examples**

Enter the query as shown in the search box. In the omnibox, put `?` first, as in `?modified:24h`.

| Query | Finds |
| --- | --- |
| `proxy` | Key names, value names, or value data containing `proxy` |
| `proxy match:names` | Values whose names contain `proxy` |
| `proxy match:keys,names` | Key names and value names containing `proxy` |
| `driver timeout match:notes` | Keys and values with saved notes containing `driver timeout` |
| `proxy match:keys,names,data,notes` | Matches for `proxy` in key names, value names, value data, or saved notes |
| `activity:edited` | Values you edited in Regedit |
| `activity:inspected within:12h` | Values you explicitly inspected in the last 12 hours |
| `activity:either within:7d` | Values you edited or inspected in the last 7 days |
| `modified:24h` | Keys last modified in the last 24 hours |
| `modified:2026-11-01..2026-11-22` | Keys last modified from November 1 through November 22, 2026 |
| `modified:2026-11-01..` | Keys last modified on or after November 1, 2026 |
| `modified:..2026-11-22` | Keys last modified on or before November 22, 2026 |
| `proxy activity:either within:7d modified:30d` | Values matching `proxy` that you edited or inspected in the last 7 days, in keys last modified in the last 30 days |
| `"activity:edited"` | The literal text `activity:edited` in names or data |

Use `within:` with `activity:` and give the window in hours (`h`) or days (`d`). For `modified:`, the rolling presets are `1h`, `24h`, `7d`, and `30d`. Custom dates use `YYYY-MM-DD` and include both endpoints in local time.

Use `match:` to select the same fields as **Match text in** in Advanced search. Choose `keys` for key names, `names` for value names, `data` for value data, and `notes` for saved notes. Separate fields with commas and omit spaces. A match in any listed field counts. The default fields are key names, value names, and value data. With `activity:`, choose `names`, `data`, or both.

To search notes from the dialog, select **Notes** and enter text in **Find what**. Results show an excerpt and open the matching key or value. A `modified:` filter uses the containing key’s last-write date.

Right-click **Back** or **Forward** to choose a previous or later visit, nearest first. The menus retain full registry paths and the 32/64-bit view; jumping within history preserves the intervening visits. **Alt+Left / Alt+Right** and mouse **X1 / X2** navigate the active registry tab. **Backspace** goes back in the list; in the tree, it opens and selects the selected key's parent, keeping keyboard focus in the tree. Text fields keep their normal editing behavior. Mouse navigation is ignored while a modal dialog or a full tool tab is active.

**Middle-click** a tree item, registry list row, favorite, note, or search result to open its key in a background tab. The current tab and selection stay in place, including an open Favorites panel. Values open their owning key with that value selected; the optional `..` row opens the parent. Existing open-key/new-tab buttons also support middle-click. Computer and 32/64-bit view are preserved. Empty space and Favorites folders do nothing; the tree chevron keeps its expansion action. Release on the same item to open it.

**Middle-click a tab header** to close that tab, including a background tab. This uses WinUI's native tab-close action, with the same cleanup and final-tab window-close behavior as the close button.

Selected tabs and their connected page surfaces share the same theme brush, including browser navigation, full tool pages, settings and search results. The registry list and its toolbar keep a solid, inset reading surface. Side and bottom panels share a quieter solid background, while the title-bar strip reveals the selected window material. These surfaces follow light/dark themes and system high-contrast colors, keeping registry data legible with either backdrop.

The unnamed **(Default)** value uses a tag icon, even when unset. Its color follows its type. Other strings use the maroon text icon, and binary and numeric values use the blue data icon. In high contrast the icons inherit the row’s foreground. Keys use a folder and the parent row uses an up arrow.

The list command row spans the list's width, with **New**, **Filter**, and **View** on the left and the **Detail panel** button just before **…** at the right edge. Tool and snapshot lists use the same toolbar alignment. Typing with the list focused reveals a filter for the current key; **Filter** or Ctrl+Alt+F also opens it. Results update after a short typing pause. Deleting the last character, clearing the text, clicking the dismiss button, or pressing Escape closes the filter and returns keyboard focus to the registry list. Navigating to another key resets the filter. Typing immediately clears the previous row selection. **View > Show keys in list** restores the combined presentation; values-only is the default, with hives still shown in the Computer overview. With keys visible, **View > Show parent key ..** optionally adds a parent-navigation row first in the list, including during sorting and filtering. Double-click it or press Enter to go up; from a hive it opens Computer. The option is off by default and remembered. This row is excluded from counts and registry mutations. **Value preview** enables the optional footer. Navigation-pane visibility and 32/64-bit registry view are also under **View**.

Right-click a column header or focus it and press Shift+F10 to choose visible columns. Drag a header to reorder it, or use **Move column left** and **Move column right** in its menu. Drag a divider to resize, or focus it and press Left or Right. The column selection and order apply to browser tabs. Each tab remembers its own widths, sort direction and navigation-pane layout.

**Key last modified** shows a local key’s last write in local time. A subkey row shows its own timestamp. Value rows share the timestamp of the key you’re browsing, including the unnamed value. Windows records one timestamp for the whole key. Refresh (F5) to read it again after an external edit. **Children** counts a key’s immediate subkeys and stored values. **Notes** shows its saved note. **Link target** shows the native destination of a local registry link or the contents of a REG_LINK value. **Size (bytes)** shows the stored value’s byte length, including zero for an empty value.

Sorting keeps the parent row first, followed by keys, the unnamed default value, and then named values. Clicking a header reverses the sort within those groups. To reclaim the command row, turn off **Settings > Registry list > Show list toolbar**. Ctrl+Alt+F and type-to-filter still work.

On an ordinary restart, Registry Editor brings the saved browser tabs back into one window. It uses the first saved window’s tool panel, normal bounds and maximized state, and fits the restored window to the available display. Opening an explicit registry address starts a separate session and preserves the previously saved tabs.

**View > Automatically sync tree** controls whether the navigation tree follows the current key. It is on by default and is also available in **Settings > Registry view**. Turn it off to keep the tree in place while navigating through the list, address field, or history. **View > Sync tree to current key** expands and selects the current location once, revealing the navigation pane if hidden, without changing the preference or navigating the list. New tabs and connection/view changes initialize their tree at the current key; turning tracking back on syncs existing registry tabs immediately.

The list's **See more** menu groups **Selection**, Paste, Search, branch tools, export/printing, and Hive commands. **Selection** reuses the row context menu, including its selection checks. Export, Permissions, and **Copy current key path** in the main overflow always target the current key; the corresponding row commands target the selected key. Hive commands are enabled only for applicable locations.

Right-clicking a registry row groups opening, copying, inspection/key tools, and rename/delete actions. Key rows offer favorites, export, and permissions; value rows offer modification, inspection, and watching. Multiple selections show the supported bulk action, and the virtual parent row exposes only navigation and path copying. Right-clicking an already-selected row preserves the group selection. Empty list space offers New, Paste, Refresh, and **Copy current key path**. Favorites groups opening, moving, and rename/removal separately, with Add current key and New folder on its background menu. Shift+F10 opens the focused row's menu. With the registry tree focused, Ctrl+C copies its selected key and path, and Ctrl+V pastes into that selected key. With the registry list focused, Ctrl+C copies its single stored key or value and Ctrl+V pastes into the current key. **Duplicate value…** copies one stored value in the current key, including a stored default value. It suggests an unused name, preserves the registry type and every byte, leaves the clipboard alone, and supports Undo and Redo. Paste asks for a copy name and preserves the registry type and bytes; it refuses an existing target or a key copied into itself or its descendants. Computer and virtual parent rows cannot be copied, and multiple list selections do not copy. Ctrl+Shift+C remains path-only. Text fields, including the address, filter, Notes and dialogs, retain their normal cut, copy and paste shortcuts.

Numeric editors show decimal and hexadecimal interpretations as you type, with the limits for the selected type. For recognized numeric literals and matching `.reg` value data, **Apply conversion** uses the selected registry type and numeric base. In the binary editor, type hex digits or paste complete byte pairs separated by spaces or line breaks. Pasting a comma-separated list or a matching `.reg` hex literal offers **Apply conversion** before editing any bytes. The byte count appears above the editor.

Search results, Journal and Watches share a bottom panel within each window. Every search opens its own tab; Journal and Watches reuse one tab each. The panel stays available when switching registry tabs. Drag its top edge, or focus the edge and press Up/Down, to change its height. **Open in main tab** expands the active result set or tool without restarting it; **Show in panel** moves it back. Hiding the panel keeps its tabs and scans. Closing a search tab cancels only that search. Drag the column boundaries, or focus a boundary and press Left/Right, to resize the list columns. Right-click a row or press Shift+F10 for its open, inspection and copy actions. The lists remain virtualized while new results append without rebuilding existing rows. The tab strip contains the commands and a live filter box, and the panel has no separate status bar. Each search tab keeps its own filter, matching names, registry paths and displayed data without restarting the scan. Clear the filter to restore all results. Counts in filtered search tab headers show visible/total matches; the tab tooltip and **Search scope** show the original scope and status.

Favorites and Notes retain their side panel. Pin it alongside the registry, drag its left edge (or focus the edge and press Left/Right) to resize it, or use **Open in tab** for the full view. Width, pinned state, and selected tool are remembered; narrow windows use an overlay. F9 toggles the right sidebar. Use the toolbar's **Tools → Bottom panel** to reopen or hide bottom tabs; opening an empty panel starts with the journal. Escape or Hide dismisses a panel and returns keyboard focus. **Settings → Toolbar buttons** lets you enable individual one-click buttons for Favorites, Notes, Watches, Change journal, Saved searches, Workspaces, Right sidebar, and Bottom panel. Every optional button is off by default. Enabled buttons apply to all registry tabs and move into overflow when space is limited; all commands remain in Tools. Panel buttons use a subtle neutral highlight while open. Right-click the toolbar or Tools and choose **Customize** to open the toolbar choices directly. Optional buttons also offer **Hide from toolbar**, which removes their shortcut without closing the tool. **Settings → List spacing** adjusts row padding from compact to comfortable in registry and tool lists without changing text size.

**Open in tab** carries the search and selection, reuses an existing tab for that tool, and closes the panel while keeping its pin preference. **Show in panel** returns to the registry with the same tool context. Opening a location from a tool tab creates a registry tab. Full tool pages use their own header instead of a registry-context navigation row.

Collection commands sit above the list. Journal groups edits by local day. Journal and Watch rows show the affected key or value first, with a short change summary. Their inspectors show the full target and registry view. In a narrow tab or panel, **Show details** opens the selected comparison. Selecting multiple changes keeps both script formats available. The Notes editor shows its target and save state.

Click a favorite in the panel or press Enter to open it in the current registry tab. An unpinned panel closes after opening. A pinned panel stays available. Right-click for commands, or use F2 to rename and Delete to remove with confirmation. The full Favorites tab supports multi-selection and double-click opening.

To copy a long favorite name or path, select the favorite, show **Details**, and press Tab to reach the name or path. Press Ctrl+A, then Ctrl+C to copy the entire field. This works in the panel and in a Favorites tab.

The full Favorites organizer keeps an expandable folder tree beside the selected folder's bookmarks. **New folder** and **Add favorite…** default to that folder. The full-tab Add dialog starts with an empty location and asks for the name, registry view and destination; it works without a registry tab. The address star shows the Add dialog for a new favorite and removes an existing one. Ctrl+D uses the selected key when the registry tree or a single key row has focus; values, multiple selections, and other browser controls use the current location. Text editors keep their normal key handling. Other open tabs at the same computer, key and registry view update immediately. The compact panel’s **Add current key** also saves the visible location. The panel uses one expandable hierarchy, starting directly with top-level folders and favorites. Its header holds Add current key, New folder, Search favorites, and item actions. Search favorites or Ctrl+F reveals the search field; Escape clears and collapses it before a second Escape closes the sidebar. Folder actions are available from its menu or a row context menu. **Move to folder…** preserves nested subtrees; Move up/down reorders siblings outside global search. Search covers all folders and shows ancestry. Optional **Details** shows bookmark information, not live registry values. Existing flat favorites remain at the root. Removing a folder confirms descendant counts; registry keys are unaffected.

Double-click a tree row to expand or collapse it. Right-click a row, or press Shift+F10 while it is focused, to open it in a new tab, add or remove its favorite, export its branch, open its Windows permissions dialog, or copy its path. These commands use the row that opened the menu, including its computer and registry view.

Each tab has a selectable **Computer** or server root above its hives. Selecting it lists the available hives in the right pane; double-click a hive or press Enter to open it. Up from a hive returns to this overview. Whole-computer Find, Export, and Print traverse HKEY_LOCAL_MACHINE and HKEY_USERS once in the selected registry view. Selecting HKCR, HKCU, or HKCC explicitly uses that alias. Exports include accessible keys and values and record skipped branches. A system backup also needs hive metadata and permissions. The address bar and tab show a 32-bit label even when the status bar is hidden. Key editing, permissions, branch capture, and watches require selecting a hive or key. Computer roots work with history, favorites, saved tabs, and workspaces; their addresses are `Computer` locally and `\\SERVER` remotely.

CommunityToolkit.Mvvm provides observable state and asynchronous navigation commands. `CommunityToolkit.WinUI.Controls.Sizers` provides the keyboard-accessible pane splitter. `CommunityToolkit.WinUI.Controls.SettingsControls` supplies the adaptive Windows-style cards and expanders for Settings. The UI uses native WinUI controls elsewhere.

## Snapshots and comparison

Use **Snapshots** in the branch toolbar (or its overflow menu):

1. **Capture branch snapshot…** saves the current branch and all its subkeys to a `.regsnap` file.
2. Change the application's setting, then choose **Compare last capture with registry**. To continue after restarting the editor, use **Compare saved snapshot with registry…** instead.
3. Select a difference and press Tab to reach **Selected target**. Ctrl+A, Ctrl+C copies the full key address and, for a value, its name. The details below it show the before/after types, data and raw bytes. **Export selected** writes a `.reg` patch for the selected differences. **Reopen last comparison** retains the result until that tab closes.

Snapshots include empty keys, default values, unknown value types, and unmodified raw bytes, plus the source computer, registry view, and capture timestamps. Comparisons always read the saved branch in its original view, even if the tab has navigated elsewhere. Key and value identities are case-insensitive, matching Windows registry semantics. Missing branches are reported as deletions; unreadable branches stop the operation instead of producing an incomplete comparison. Capture and comparison run off the UI thread and can be canceled with Esc or the notification's Cancel button. The results list uses the native virtualizing list panel.

A `.reg` file cannot select a registry view. The exported patch includes the required `reg.exe import FILE /reg:32` or `/reg:64` instruction. Selecting a removed key also requires selecting all its descendant changes, because importing a key deletion removes the entire subtree. Exporting a diff does not modify the registry.

Snapshots are observations collected over time, not atomic transactions; pause other writes when an exact stable baseline matters. They store keys and values, not permissions or key timestamps. Files contain the captured data without encryption and are limited to 256 MiB. Local snapshots are compared on their source computer; opening a remote snapshot requires confirming the connection before sending Windows credentials. Data previews are shortened for large values; exports retain every byte.

## Investigation tools

The application menu's **Tools** submenu opens **Favorites**, **Notes**, **Watches**, **Change journal**, **Saved searches**, and **Workspaces**. Use **Open in tab** in Favorites or Notes for the full organizer. The registry list's **See more** menu offers **Watch this branch** and **Notes for this key** for the current location. Results use native virtualized lists in separate tabs, so opening a key does not discard an investigation.

- **Change journal.** Key and value edits save a before-state before writing and an after-state when the operation finishes. Select one completed edit and choose **Revert edit…**. The journal checks later edits and the current data, including local hive aliases and shared 32-bit and 64-bit keys. Unrelated values and independently redirected local keys can be reverted separately. On a remote connection, a later change to a branch or the same value name can block Revert even when its path differs. Windows cannot confirm those backing keys are independent. Revert records its own entry. Failed, incomplete, unknown and already-reverted edits remain available for inspection.
- In Journal and Watches, select a row for its before-and-after comparison. Enter and double-click open the details in a narrow panel or tab. **Compare raw bytes** shows the first changed offset and up to 16 bytes on each side. **Inspect full value** opens the value inspector, and **Copy exact bytes** copies the complete side as hexadecimal.
- Select **Archive edit** to hide an entry from the active list, or **Archive all edits** in the Journal overflow menu to archive every active entry across all locations, regardless of filters. Under **History filters**, choose **Archived** to find it and **Unarchive edit** to bring it back. Select an edit to follow its original, revert or blocking later edit. Following a link reveals the target even if it was filtered out. Archived entries continue to protect later changes.
- If another edit holds the journal, Registry Editor waits up to three seconds. A busy-journal message means the edit waiting for the lock has not written to the registry. Retry after the other operation finishes.
- An edit can capture up to 64 MiB across its before- and after-states, including value data, names and key permissions. If the before-state or the known size of the proposed edit exceeds that budget, Registry Editor leaves the registry untouched. If saving the final state fails after a change, the journal retains the before-state as an incomplete entry. Inspect it before deciding how to recover.
- If a journal file is damaged, the other readable entries stay visible. Automatic Revert requires the complete history. Imports, hive load/unload and permissions changes record activity and block earlier reverts on that computer. Recreated keys restore owner, group and access rules. Auditing entries and other key metadata require separate recovery. Other programs can still change the registry during a revert. Each local journal file has a 256 MiB limit and uses the storage permissions described below.
- **Live watch.** Choose **Watch branch**, **Watch value**, or **New watch** and check the proposed key or value, computer and registry view. Watches are local to the window. Closing a panel or tab keeps capture running. Rapid writes can coalesce over a 125 ms sampling interval. Each observation retains its timestamp, location, type and raw before- and after-data. Windows does not identify the writer. Scroll back to read earlier observations, use **Pause viewing** to freeze that surface, and choose **Follow latest** to return to the tail. **Stop watching** keeps the observations. **Restart** creates a fresh baseline and keeps the earlier session. **Remove** discards a stopped session. Deleting the target, losing access or reaching the 10,000-observation limit ends the watch. Open the window’s unexpected-stop notice to inspect the affected session.
- **Find all.** Find opens a result-set tab in the bottom panel and displays matches progressively. A thin indeterminate bar overlays the top of the list while scanning and does not move the rows. Enter type names or numeric codes, separated by commas, to filter values. Enter, double-click and **Open selected result** reuse a browser destination for that result set, initially the originating registry tab. Navigating elsewhere in the destination releases it. **Open in new tab** opens a foreground tab, and middle-click opens a background tab. Each route selects the matching key or value in its original registry view. **Cancel search** stops a running scan and retains displayed matches. **Run search** refreshes the current result set. **Save search** retains scope, view, query, flags and type filters. Choose **Saved searches** to run it again. F3 advances the selected row in the active result set and opens its key or value in the current registry tab, including a newly opened tab. It follows the displayed result filter and leaves a hidden bottom panel hidden. Selecting another result set changes F3 for every registry tab in that window; selecting Journal or Watches retains the last selected search. Progress and the end of the list appear in the status bar. A search reports skipped keys and shows a notice when it reaches 100,000 matches.
- **Value inspector:** Select a stored value and use **Inspect value…** from its context menu (Shift+F10), or press Ctrl+I with the registry list focused. Use the **Detail panel** toolbar toggle or Alt+P to show or close the resizable pane, which follows the selected key or value without moving focus. Inspect uses this pane while it is open; with the pane closed, Inspect opens a dialog. It sits beside the list in a wide workspace and below it in a narrow one. Keys and hives show their name and full path. An unset default value explicitly shows **(value not set)** and has no byte tools. With no list row selected, the pane shows the current key. Selecting multiple rows clears the contents while keeping the pane open; its Close button returns focus to the list. The small value preview is hidden while the inspector is open. Journal and Watches retain dialog inspection of their recorded values. The inspector shows the full value name and a metadata grid above the value data. Captioned readouts have individual copy buttons. Below the value and its actions, the notes section shows the direct **Value note** first or a **Shared note** with its scope. **Open key note** opens an existing note for the containing key. Use **Edit** to open the full note and expand the shared notes to see additional matches. String, expandable-string, link and multi-string values also provide a read-only **Value data (full contents)** field, even when the optional value preview is hidden; focus it and use Ctrl+A, Ctrl+C to copy the displayed contents. Rows, tooltips and accessible names use short text previews; filtering, data sorting, Copy and export retain the full value. Binary row previews retain the first 256 whole bytes with a continuation indicator. The Inspector shows up to 32 bytes of hexadecimal for values without a text or numeric readout. DWORD/QWORD values show unsigned decimal, hexadecimal, and bits simultaneously. Choose **Interpret** to open a dialog with a highlighted hex view. Set a byte offset and count, then choose a format for text, Windows/network-order GUIDs, Unix timestamps, or FILETIME. Text decoding rejects invalid sequences. The hex and ASCII view scrolls to the selected range. **Raw** opens the full bytes in a read-only dialog with the current range highlighted. Interpretation can address any range of up to 65,536 bytes.
- **Reveal in File Explorer.** The value context menu and browser inspector offer Reveal for local REG_SZ and REG_EXPAND_SZ values containing a complete drive-qualified path. Whole-path quotes are accepted, and REG_EXPAND_SZ environment variables expand on this computer. Files are selected in their parent folder; folders open directly. Missing targets report an error. Remote paths, network shares and command-line parsing are outside this first version.
- **Notes.** Right-click a value and choose **Add note** or **Edit note**. The note marker beside a key or value name previews the first line of its note and opens the editor. Key notes and value notes belong to a computer and registry view. The sidebar and Notes organizer share one draft. Notes save automatically after a one-second pause, when switching notes or closing their view, and before closing the window. If saving fails, use **Retry** or Ctrl+S. **Delete note** asks for confirmation. Clearing the editor also removes the saved note, and **Undo** restores it while the draft is open.
- To reuse a value note, choose **Show on matching values** from the editor menu. Select a branch to match the same value name in that key and its subkeys. You can require a registry type or byte length. Choose **Save scope** to apply the rule. The scope keeps the original computer and registry view. Open the **Notes organizer** from the sidebar header to search all notes by name, path, or note text. Select a note to edit, open its registry target, or copy its text from the toolbar. **Copy path** and **Delete note** are in the toolbar overflow menu.
- **Workspaces.** A workspace stores up to 64 tabs, searches and watch definitions. **Open workspace** lists the exact targets before applying changes. **Add workspace** keeps current tabs and opens saved search tabs ready to run. Select **Start saved searches and watches** to run them immediately. An existing running watch on the same target is reused. **Replace browser and search tabs** keeps tool tabs, note drafts and existing watches. If a target fails during preparation, its error is shown and current tabs stay available alongside the successful additions. Watch observations stay in their original window. Saving with an existing name updates the saved search or workspace.
- **Copy as script:** Select snapshot differences, journal entries, or watch observations and choose **Copy PowerShell** or **Copy reg.exe (.cmd)**. PowerShell preserves raw bytes, uncommon types, remote targets, and the explicit registry view. Save the reg.exe variant as UTF-8 `.cmd`; it escapes batch expansion and preserves supported value types and views. Data that reg.exe cannot represent exactly, including malformed strings, raw REG_NONE, and unsupported names/types, is rejected with a prompt to use PowerShell. Copying only changes the clipboard. Review scripts before running; they apply the selected after-state without the journal's conflict checks.

Settings, notes, searches, and workspaces live in `%LOCALAPPDATA%\RegeditModern\settings.json`. Multiple windows coordinate saves: independent changes are combined, while conflicting edits or deletions of the same item are rejected without replacing the saved file. An unsaved note remains available in its editor after a conflict. Other windows do not refresh automatically; reopen a window to load the latest saved state before retrying. Each window saves its own tabs, and the next ordinary launch restores the combined sessions. A window opened for a specific registry link does not replace the other saved tabs. Close all older app builds before using this behavior because they do not participate in coordinated saving.

Unelevated journal entries remain in the settings folder's `Journal` subfolder. Administrator windows use a separate persistent store at `%PROGRAMDATA%\RegeditModern.AdministratorJournal\<Windows user SID>`, owned by Administrators and accessible only to Administrators and SYSTEM. An ordinary process of the same user cannot read or modify that store. The Journal page and panel identify which history is being shown; each mode reads and reverts its own history. Administrator history is protected by Windows permissions, not encryption, and other administrators can access it. Both stores are retained until removed. Unsafe ownership, permissions, or redirected paths block edits before recording or mutation; the app never falls back to recording administrator data in the ordinary user profile.

Existing journal files from older versions stay in their original location and are not automatically migrated or deleted. The new protection does not retroactively protect any sensitive data already recorded there. Registry editing is still governed by your Windows permissions.

## Keyboard

Each registry tab has its own **Undo** and **Redo** commands in the list toolbar. Toolbar labels stay fixed; their tooltips identify the next action and show its full path, computer, and registry view. Registry context menus include the action in their labels. Navigation, filtering, sorting, and selection do not change the history. Deleting several selected items is one step. A new edit clears that tab's Redo. Duplicated, reopened, and restored tabs start with empty edit histories; the persistent Change journal remains available after a tab closes.

**Ctrl+Z / Ctrl+Y** undo or redo committed registry edits while the registry tree, list, or command bar has focus. Text fields, including inline rename, the address bar, filters, value editors, and Notes, keep their own text editing behavior. An empty text Undo history never falls through to registry Undo. Other tool views do not undo edits in a background registry tab.

Undo and Redo check all items in an action before starting. Later overlapping edits, external changes, imports, hive operations, or permissions activity can block an earlier action; the command explains the conflict and offers **Show in Journal**. It never skips a blocked action. Key operations ask for confirmation, and a partial failure stops the history chain for review. Registry writes are not atomic; recreated keys restore owner, group, and access rules, but not auditing or other key metadata. A successful Undo or Redo refreshes open views and offers **Go to change**.

String and expandable-string values open in single-line mode. Select **Allow line breaks** in the current dialog for multiline input; existing multiline values open with it selected. Multiline paste is blocked until this is selected. Remove existing line breaks before turning it off.

Enter submits a single-line field in an edit, rename, create, or save form. In a multiline value field, Enter inserts a line break. Delete confirmations default to Cancel. With the list focused and no row selected, Delete and F2 leave the current key untouched.

Choose **Application menu > Keyboard shortcuts** to open the grouped reference inside the existing Settings tab. The same page is available from **Settings > Help > Keyboard shortcuts**. Its Back button returns to preferences, and opening it repeatedly reuses the Settings tab.

| Shortcut | Action |
| --- | --- |
| Alt+F | Open the application menu |
| F10 | Focus the application menu button |
| Ctrl+, | Open Settings |
| Ctrl+/ | Open Keyboard shortcuts |
| F1 | Open Help and about |
| Alt+N / Alt+I / Alt+V / Alt+M | New / Filter / View / toolbar overflow |
| Alt+P | Show or close the detail panel |
| Alt+B / Alt+O / Alt+U / Alt+R | Back / forward / parent / refresh |
| Alt+E / Alt+K / Alt+S / Alt+A / Alt+T | Edit address / toggle favorite / start search / advanced search / tools |
| Ctrl+T / Ctrl+W | New / close tab |
| Ctrl+Shift+T | Reopen the last closed registry tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+L / Alt+D | Edit and select the address |
| Esc while editing the address | Discard the draft and show the current address |
| Alt+Left / Alt+Right / Alt+Up | Back / forward / parent |
| Backspace | Back in the list; open and select the parent in the tree; edit normally in text fields |
| Ctrl+Shift+H | Open the current key's path in the other local hive, HKLM or HKCU |
| Alt+Shift+L | Sync the navigation tree to the current key, revealing it if hidden |
| Ctrl+F / Ctrl+Shift+F / F3 | Focus search / advanced search / find next |
| Ctrl+Alt+F | Filter the current key's list |
| F9 | Open or close the right sidebar |
| Ctrl+J | Open or close the bottom panel |
| Ctrl+Shift+B | Show or hide the Favorites bar |
| Ctrl+Shift+O | Open Favorites in the right sidebar |
| Esc in the tool panel | Close the panel and return focus |
| Ctrl+S in Notes | Save the current note immediately or retry a failed save |
| F5 / F6 | Refresh / cycle focus through the tree, list, open detail panel, right sidebar, and bottom panel |
| Ctrl+C / Ctrl+V in the tree or list | Copy the selected registry item / paste into the focused tree key or current list key |
| Ctrl+Z / Ctrl+Y in the registry tree, list, or command bar | Undo / redo the last committed edit made through this tab |
| Ctrl+Shift+C | Copy the focused key path or the current address |
| Ctrl+I in the list | Inspect the selected value in the open detail panel, or in a dialog when the panel is closed |
| Ctrl+N in the list | Open the New key/value menu |
| Ctrl+D | Add or remove the favorite for the focused tree key, single selected key row, or current location |
| Enter / F2 / Delete | Open or edit / rename / delete |
| Esc in a registry tab | Clear the open list filter first. Otherwise cancel result navigation, export, snapshot work or print preparation. |

## Data and permissions

Writes apply immediately to the Windows registry. Deletions and file imports require confirmation. Before writing, renaming or deleting an existing value, the editor checks it against the version you opened. Other processes can still change it between that check and the write.

When a key name already exists, the rename form keeps the name you typed so you can correct it. If Windows denies a local edit in a standard window, the form identifies the affected key and registry view and offers **Open as administrator**. Windows opens a separate editor at that key and view if you approve elevation. The original window retains your draft. Review the key's permissions if access is still denied.

With Settings or a tool tab active, **Export entire registry…** and **Import registry file…** use the most recently active registry tab's computer and registry view. With no registry tabs they use the local 64-bit view. Import requires a local connection.

**Import registry file…** opens a separate, resizable preview window. You can also drop one `.reg` file onto the main window to open the same preview, using the current local registry view. Dropping a file does not import it automatically. Drag and drop requires a non-administrator window; use the menu when running as administrator. Edit the source with registry syntax highlighting in WinUIEdit on the left and inspect the read-only preview on the right, with keys above their value instructions. Deleted folders use a maroon folder icon and a Delete label; delete-then-recreate folders use an amber reset label. Ancestor deletions affect their children. Repeated writes and deletions remain in source order; this is a preview of file instructions, not a comparison with the current registry. Selecting a key or value reveals its source line. **Open key in registry** opens a normal registry tab. File errors appear in a collapsible **Problems** list below the editor and as gutter markers. Select a problem or click its marker to jump to that source line. The error count in the status bar or **Alt+D** reopens Problems, including from preview-only mode. Editing updates the list without moving the caret. Import stays disabled until all errors are resolved.

The **View layout** selector beside Import switches between **Editor only**, **Editor and preview**, and **Preview only**. Returning to both restores the split position. Switching views keeps source edits, selection and Undo history.

**File > Settings…** opens **Registry file editor** in Settings. Choose the default view for new preview windows and the editor's font family and size in points. Font changes apply to open editors without resetting their text or Undo history. The initial default view is **Editor and preview**.

Press **Alt** for key hints. **Alt+F** opens File; within it, **O** opens a file, **R** reloads, **S** saves, **A** opens Save as, **T** opens editor settings, and **C** closes the preview. **Alt+1**, **Alt+2**, and **Alt+3** switch to editor, split, and preview. **Ctrl+,** opens editor settings; **Ctrl+W** or **File > Close** closes the preview and offers to save unsaved changes.

The **File** menu contains **Open** (Ctrl+O), **Reload**, **Save** (Ctrl+S), and **Save as** (Ctrl+Shift+S). The toolbar has **Undo** (Ctrl+Z) and **Redo** (Ctrl+Y or Ctrl+Shift+Z) for source edits. Saving preserves its encoding and line endings. If the file changed outside the app, reload it or save your edits to another file. Unsaved edits require a save/discard/cancel decision before opening another file or closing the window. Preview supports files up to 10 MiB, in UTF-16 or UTF-8 with a byte order mark, or Windows ANSI.

Optional header comments appear in a collapsible **About this file** panel above the preview tree:

    ;Title=Registry preview sample
    ;Description=Preview changes before importing.
    ;Author=UI fixture
    ;Requires=ExplorerRestart

Recognized names are case-insensitive and must appear before the first key section. The last Title, Description or Author wins; repeated Requires entries accumulate without duplication. Supported actions are ExplorerRestart, Restart and Logoff. Unknown directives and actions are ignored. These are notes supplied by the file. Successful import repeats its follow-up recommendations, including unsaved changes; it does not perform those actions.

Choose **32-bit** or **64-bit** in the preview's status bar before **Import…**. Import confirms the target and applies the current source, including unsaved edits, without saving the file. It records journal activity, has no automatic revert, and can partially succeed before an error. Opening or editing a preview does not write to the registry.

Export and hive-save operations first write a temporary sibling file and replace the destination after success. Selected-branch export fails on an unreadable key. Whole-registry export skips inaccessible branches, annotates them in the file, and reports the count. A `.reg` backup contains keys and values, not permissions. Use a native hive backup when preserving the security descriptor is necessary. Names containing line breaks cannot be safely represented in `.reg` syntax and require a hive backup.

Imports use Windows' parser and may make partial changes before reporting a failure. Key copies require the same computer connection and registry view. A failed copy can leave a partial destination. Remote access requires Windows Remote Registry connectivity and suitable credentials. Cancellation takes effect between Windows RPC calls.

**Connect network registry** first uses your Windows identity. If sign-in fails or access is denied, Windows asks for another account and the app retries. Successful credentials remain as a Windows logon token in memory while a browser or search tab for that computer is open in any window, or while an operation still uses that computer. Closing the last such tab, or navigating it away from that computer, forgets the sign-in once ongoing operations finish so the next connection can use another account. Passwords are cleared from temporary buffers and are not saved in settings. The connection opens the computer's hive list, so an account with access to HKEY_USERS can connect even when HKEY_LOCAL_MACHINE is restricted. Individual keys and edits still require permission on the remote computer.

Settings live in `%LOCALAPPDATA%\RegeditModern`. Registry Editor preserves unreadable settings as a separate backup before replacement. The application uses no telemetry or network service. Network access occurs when you connect to a remote registry or explicitly open a web search in your browser. Web search sends the selected path and value name to the configured search engine after removing the remote computer name and anonymizing the user SID directly under HKEY_USERS.

Normal and elevated sessions for the same Windows account write diagnostic logs to `%LOCALAPPDATA%\RegeditModern\Logs`. Registry Editor keeps the seven newest files and starts a new file each day and when a file reaches 5 MiB. Logs stay on this computer. Failure entries include the operation, exception type, error code and call stack. Registry contents, launch arguments and note text are excluded.

See [Development](../dev/readme.md) for build commands, or return to the [project README](../../README.md).
