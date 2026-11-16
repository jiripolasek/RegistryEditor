(() => {
  const storageKey = 'registry-editor-theme';
  const systemTheme = window.matchMedia('(prefers-color-scheme: dark)');
  const root = document.documentElement;
  let preference = 'auto';

  function readPreference() {
    try {
      return localStorage.getItem(storageKey) ?? 'auto';
    } catch {
      return 'auto';
    }
  }

  function getTheme() {
    return preference === 'auto' ? (systemTheme.matches ? 'dark' : 'light') : preference;
  }

  function applyTheme(value) {
    preference = ['light', 'dark', 'auto'].includes(value) ? value : 'auto';
    const resolved = getTheme();
    root.dataset.theme = preference;
    root.dataset.resolvedTheme = resolved;
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', resolved === 'dark' ? '#142a2e' : '#e6eff0');
    document.querySelectorAll('[data-theme-toggle]').forEach((control) => {
      const label = `Theme: ${resolved}. Switch to ${resolved === 'light' ? 'dark' : 'light'} theme`;
      control.setAttribute('aria-label', label);
      control.setAttribute('title', label);
    });
  }

  function setPreference(value) {
    applyTheme(value);
    try {
      localStorage.setItem(storageKey, preference);
    } catch {
      // Keep the current theme when storage is unavailable.
    }
  }

  async function toggleTheme(event) {
    const next = getTheme() === 'light' ? 'dark' : 'light';
    if (!document.startViewTransition || window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
      setPreference(next);
      return;
    }

    const rect = event.currentTarget.getBoundingClientRect();
    const x = event.detail === 0 ? rect.left + rect.width / 2 : event.clientX;
    const y = event.detail === 0 ? rect.top + rect.height / 2 : event.clientY;
    const radius = Math.ceil(Math.hypot(Math.max(x, window.innerWidth - x), Math.max(y, window.innerHeight - y)) / 0.7);
    const transition = document.startViewTransition(() => setPreference(next));
    try {
      await transition.ready;
      root.animate({
        maskImage: ['radial-gradient(circle, #000 70%, transparent)', 'radial-gradient(circle, #000 70%, transparent)'],
        maskRepeat: ['no-repeat', 'no-repeat'],
        maskPosition: [`${x}px ${y}px`, `${x - radius}px ${y - radius}px`],
        maskSize: ['0px 0px', `${2 * radius}px ${2 * radius}px`],
      }, { duration: 500, easing: 'ease-in', pseudoElement: '::view-transition-new(root)' });
    } catch {
      // An interrupted reveal still applies the requested theme.
    }
  }

  applyTheme(readPreference());
  document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('[data-theme-control]').forEach((control) => control.removeAttribute('hidden'));
    document.querySelectorAll('[data-theme-toggle]').forEach((control) => control.addEventListener('click', toggleTheme));
    applyTheme(preference);
  }, { once: true });
  systemTheme.addEventListener('change', () => {
    if (preference === 'auto') applyTheme(preference);
  });
  window.addEventListener('storage', (event) => {
    if (event.key === storageKey || event.key === null) applyTheme(readPreference());
  });
})();
