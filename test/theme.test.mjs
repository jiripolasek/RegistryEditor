import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

const source = readFileSync(new URL('../src/scripts/theme.js', import.meta.url), 'utf8');

function setup({ saved = null, dark = false, blocked = false, reduced = false, transition = false } = {}) {
  const events = {};
  const attributes = {};
  const system = { matches: dark, addEventListener: (_, listener) => { events.system = listener; } };
  const root = { dataset: {}, animate: (...args) => { events.animation = args; } };
  const button = {
    setAttribute: (name, value) => { attributes[name] = value; },
    addEventListener: (_, listener) => { events.click = listener; },
    getBoundingClientRect: () => ({ left: 10, top: 10, width: 40, height: 40 }),
  };
  const storage = {
    value: saved,
    getItem() { if (blocked) throw new Error('Storage unavailable'); return this.value; },
    setItem(key, value) { if (blocked) throw new Error('Storage unavailable'); this.key = key; this.value = value; },
  };
  const document = {
    documentElement: root,
    querySelector: () => ({ setAttribute: (_, value) => { events.color = value; } }),
    querySelectorAll: (selector) => selector === '[data-theme-toggle]' ? [button] : [{ removeAttribute: () => {} }],
    addEventListener: (_, listener) => { events.ready = listener; },
  };
  if (transition) document.startViewTransition = (update) => { events.transition = true; update(); return { ready: Promise.resolve() }; };
  runInNewContext(source, {
    document,
    localStorage: storage,
    window: {
      innerWidth: 1200, innerHeight: 800,
      matchMedia: (query) => query.includes('reduced-motion') ? { matches: reduced } : system,
      addEventListener: (_, listener) => { events.storage = listener; },
    },
  });
  events.ready();
  return { root, system, storage, attributes, events, click: () => events.click({ detail: 0, currentTarget: button }) };
}

test('automatic appearance follows system changes', () => {
  const page = setup();
  assert.equal(page.root.dataset.theme, 'auto');
  assert.equal(page.root.dataset.resolvedTheme, 'light');
  page.system.matches = true;
  page.events.system();
  assert.equal(page.root.dataset.resolvedTheme, 'dark');
  assert.equal(page.events.color, '#142a2e');
});

test('manual choice persists and overrides the system on reload', async () => {
  const page = setup({ dark: true });
  await page.click();
  assert.equal(page.storage.key, 'registry-editor-theme');
  assert.equal(page.storage.value, 'light');
  assert.equal(page.attributes['aria-label'], 'Theme: light. Switch to dark theme');
  assert.equal(setup({ saved: page.storage.value, dark: true }).root.dataset.resolvedTheme, 'light');
});

test('invalid preferences fall back to the system and blocked storage still permits switching', async () => {
  assert.equal(setup({ saved: 'unexpected', dark: true }).root.dataset.resolvedTheme, 'dark');
  const page = setup({ blocked: true });
  await page.click();
  assert.equal(page.root.dataset.resolvedTheme, 'dark');
});

test('storage updates synchronize appearance and clearing storage restores automatic mode', () => {
  const page = setup();
  page.storage.value = 'dark';
  page.events.storage({ key: 'registry-editor-theme' });
  assert.equal(page.root.dataset.resolvedTheme, 'dark');
  page.storage.value = null;
  page.events.storage({ key: null });
  assert.equal(page.root.dataset.theme, 'auto');
  assert.equal(page.root.dataset.resolvedTheme, 'light');
});

test('keyboard activation reveals from the button center', async () => {
  const page = setup({ transition: true });
  await page.click();
  assert.equal(page.root.dataset.resolvedTheme, 'dark');
  assert.equal(page.events.animation[0].maskPosition[0], '30px 30px');
  assert.equal(page.events.animation[1].pseudoElement, '::view-transition-new(root)');
});

test('reduced motion applies the theme without a transition', async () => {
  const page = setup({ transition: true, reduced: true });
  await page.click();
  assert.equal(page.root.dataset.resolvedTheme, 'dark');
  assert.equal(page.events.transition, undefined);
});
