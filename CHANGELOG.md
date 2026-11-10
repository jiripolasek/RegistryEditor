# Changelog

## 0.1.0 - 2026-11-16

Initial release of Modern Registry Editor.

### Added

- Native Windows registry editor with 32-bit and 64-bit registry views, a lazy-loading tree, breadcrumbs, address suggestions, and independent navigation history for each tab.
- Tab reordering and duplication, tab context menus, reopening closed registry tabs, middle-click navigation, and session restoration.
- Flexible registry path input and **Copy path as…** for PowerShell drive paths, provider paths, and `.reg` headers. Switch between matching local HKCU and HKLM paths or follow supported registry links.
- Sortable, filterable lists with resizable, reorderable, and selectable columns, including key last-modified times, child counts, notes, and link targets. Remembered layouts, optional parent rows, and automatic or manual tree synchronization.
- Create, copy, delete, and rename keys and values, including inline rename and value duplication that preserves the type and exact bytes.
- String, multi-string, DWORD, QWORD, and raw-byte editors, including uncommon registry types. Hex and ASCII views, decimal and hexadecimal numeric readouts, optional input conversions, and explicit multiline string editing.
- Per-tab Undo and Redo with Ctrl+Z and Ctrl+Y, conflict checks, and links to blocked edits in the journal. Text fields retain their own editing shortcuts.
- Progressive registry searches with value-type filters, cancellation that retains matches, named saved searches, result filtering, and F3 navigation through the active result set.
- Resizable value inspector beside or below the registry list, with full text, hex and ASCII data, numeric interpretations, GUIDs, and timestamps. Reveal supported local file paths in File Explorer.
- Branch snapshots, comparison of saved snapshots with the live registry, and export of selected differences as `.reg` patches.
- Persistent change journal with before-and-after data, conflict-checked revert, archive filters, and raw-byte comparison.
- Live watches for local keys and individual values, with timestamped before-and-after data, pause viewing, follow latest, stop, and restart controls.
- Copy selected snapshot differences, journal entries, or watch observations as PowerShell or `reg.exe` scripts.
- Favorites organized into folders, searchable key notes with automatic saving and retry, and named workspaces containing registry tabs, searches, and watch definitions.
- Favorites and Notes in a resizable right sidebar, plus a shared bottom panel for Search, Journal, and Watches. Move tools between panels and main tabs without losing their context.
- Import and export `.reg` files, export the entire registry, save/load/unload native hives, and print a key, branch, or the entire registry.
- Native Windows permissions dialogs, administrator launch, `regedit2://` links, and taskbar Jump List shortcuts for new windows and favorites.
- Preview support for remote registry connections, including alternate Windows credentials kept in memory while the connection is in use.
- Configurable web search for key paths and value names using Bing, Google, DuckDuckGo, or a custom URL. Queries exclude value data and remote computer names and anonymize user SIDs under HKEY_USERS.
- System, light, and dark appearance with Mica or Acrylic backdrops, customizable toolbar buttons, data fonts, list spacing, address display, and status bar visibility.
- Keyboard navigation, an in-app shortcut reference, Help and About, Fluent icons, and teal cube-pyramid app artwork.
- Local diagnostic logs, recovery backups for unreadable settings, and separate user and developer documentation.

### Packaging

- Self-contained Native AOT release build profiles for x64 and ARM64, with portable ZIP archives and separate debug symbols.
- Inno Setup installer tooling for per-user or per-machine installation, protocol registration, upgrades, and uninstall that retains user settings, with production signing support.
