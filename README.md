# Registry Editor website

An Astro static site with a Fluent-inspired design, responsive layout, and automatic light/dark appearance. This `pages` branch has independent history from the desktop app.

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
- The registry interface is an illustration, not a screenshot or an interactive registry editor.
- The logo comes from the desktop app's `art/logo.svg`.
- The design follows [Fluent typography](https://fluent2.microsoft.design/typography/) and [semantic color tokens](https://fluent2.microsoft.design/design-tokens/), using CSS without a UI framework.
- Product and documentation links point to the desktop app's `master` branch.

This setup does not configure deployment or publish the site. Build instructions are linked because the app currently has no installer.

Licensed under Apache 2.0; see `LICENSE.txt`.
