# Modern Registry Editor website

An Astro static site with a Fluent-inspired design, responsive layout, and system-aware light/dark appearance with a persistent theme toggle. This `pages` branch has independent history from the desktop app.

## Develop

Requires Node.js 22.23.4 or a newer supported LTS version and npm.

```powershell
npm ci
npm run dev
```

Open the local URL printed by Astro, including `/RegistryEditor/`.

```powershell
npm run check
npm test
npm run preview
```

The static output is in `dist/`. `astro.config.mjs` uses the repository's GitHub Pages URL and base path. Change `site` and `base` together if hosting somewhere else. Asset URLs use Astro's base path.

## Content and design

- Edit the landing page in `src/pages/index.astro` and styles in `src/styles/global.css`.
- The palette builds on the original #347886 teal, using stronger teal for primary actions, watches, and workspace tools. Clear blue groups search and inspection; richer violet groups editing and comparisons. Feature previews and the source-build panel use stronger supporting tints with light and dark variants. Cool off-white backgrounds and a restrained teal-to-slate-blue hero add contrast without yellow or cream.
- The hero uses the reviewed capture-browser.png demo window directly at its native 1460 by 928 aspect ratio, with a selected DWORD and numeric preview. Its opening size stays within the source resolution; layout resizing avoids enlarging a composited bitmap. Full-window captures keep the native 8px corner radius proportionally. Feature captures mix full windows with exact pixel crops; their captions describe the visible state. PNG exports preserve UI text and omit source metadata. Select a feature screenshot to open its full-size image.
- The feature navigation becomes a sticky, opaque header after the hero. Section anchors leave room below it; narrow screens use two rows of three links.
- Reviewed captures use `src/components/ScreenshotCapture.astro`; unfinished slots use `ScreenshotPlaceholder.astro`. Use synthetic demo data, inspect every final export for private identifiers, and keep raw captures, registry backups, settings, and capture notes outside the repository.
- Feature order: search, inspection, editing, snapshot comparison, live watches, and workspace customization. Editing and comparison currently use explicit screenshot placeholders. Stage editing with Undo and the journal in the main window, rather than a standalone journal-detail close-up. The watch image is one continuous crop with registry context fading into the panel and the real window bottom retained.
- Density belongs to workspace customization alongside columns, data fonts, and toolbar buttons. A later supporting capture can compare compact and comfortable spacing at the same window size and location; align those labels with the app terminology when staging.
- Describe Undo/Redo as supported per-tab edits with conflict checks, not universal recovery. Keep remote registry connections labeled as a preview.
- Headings use self-hosted Sora variable fonts; body text and small labels use the Segoe UI font stack. Sora is licensed under the SIL Open Font License, included in `public/notices/Sora-OFL.txt`.
- UI icons are individual SVG imports from [Microsoft Fluent System Icons](https://github.com/microsoft/fluentui-system-icons), using the Regular style. The files in `src/assets/icons/` replace the upstream fixed fill with `currentColor`. No icon font is required; the MIT license is included in `public/notices/Fluent-System-Icons-LICENSE.txt`.
- The logo comes from the desktop app's `art/logo.svg`.
- The design follows [Fluent typography](https://fluent2.microsoft.design/typography/) and [semantic color tokens](https://fluent2.microsoft.design/design-tokens/), using CSS without a UI framework.
- The creator section, local creator mark, theme switching, and two-row footer follow https://mediacontrols.jiripolasek.com/. The creator mark uses the site accent through a CSS mask, and its GitHub link points to Registry Editor. The toggle remembers the choice in `registry-editor-theme`, follows the system until a choice is made, and respects reduced motion.
- Hero motion stays within one sticky introduction, inspired by https://code.visualstudio.com/. The header blends into the centered opening without a divider. Its background is an optimized local copy of the [user-selected WallpaperHub landscape](https://cdn.wallpaperhub.app/cloudcache/f/7/6/b/8/9/f76b8904797759d72d93810eb6b29b8f84e03ce3.jpg), softened with a theme-aware overlay and faded out with the opening surface. A large centered app preview moves and shrinks into the dark text-left, image-right layout, then holds before normal page scrolling resumes. The same image is used throughout. Native CSS view timelines drive the sequence without scroll interception or dependencies; keyboard focus shows the final layout immediately. Small or short viewports, reduced-motion preferences, and browsers without view-timeline support retain the static hero with a more subdued landscape background. Feature sections keep their existing layout and hover feedback.
- Product and documentation links point to the desktop app's `master` branch.

This setup does not configure deployment or publish the site. Build instructions are linked because the app currently has no installer.

Licensed under Apache 2.0; see `LICENSE.txt`.
