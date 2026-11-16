import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';

const html = readFileSync(new URL('../dist/index.html', import.meta.url), 'utf8');
const origin = new URL(process.env.SITE_URL || 'https://jiripolasek.github.io').origin;
const base = `${(process.env.BASE_PATH || '/RegistryEditor').replace(/\/$/, '')}/`;

test('built page resolves local assets and navigation under the configured site base', () => {
  const candidates = [...html.matchAll(/srcset="([^"]+)"/g)].flatMap((match) => match[1].split(',').map((candidate) => candidate.trim().split(/\s+/)[0]));
  const links = [...html.matchAll(/(?:href|src)="([^"]+)"/g)].map((match) => match[1]).concat(candidates);
  assert.ok(links.some((link) => link.endsWith('/logo.svg')));
  assert.ok(links.some((link) => link.endsWith('.css')));

  for (const link of links) {
    assert.ok(!link.startsWith('//'), `Protocol-relative URL: ${link}`);
    if (link.startsWith('#')) {
      assert.ok(html.includes(`id="${link.slice(1)}"`), `Missing anchor: ${link}`);
      continue;
    }

    const url = new URL(link, origin + base);
    if (url.origin !== origin) continue;

    assert.ok(url.pathname.startsWith(base), `Missing base path: ${link}`);
    assert.ok(!url.pathname.includes('//'), `Double slash in asset path: ${link}`);
    const path = url.pathname.slice(base.length) || 'index.html';
    assert.ok(existsSync(resolve('dist', decodeURIComponent(path))), `Missing output: ${link}`);
  }
});
