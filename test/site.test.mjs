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


test('social previews use absolute site URLs and the published image dimensions', () => {
  const meta = new Map([...html.matchAll(/<meta (?:name|property)="([^"]+)" content="([^"]*)"/g)].map((match) => [match[1], match[2]]));
  const canonical = new URL(base, origin).href;
  const imageUrl = new URL(`${base}social-preview.png`, origin).href;
  assert.equal(html.match(/<link rel="canonical" href="([^"]+)"/)?.[1], canonical);
  assert.equal(meta.get('og:url'), canonical);
  assert.equal(meta.get('og:type'), 'website');
  assert.equal(meta.get('og:title'), html.match(/<title>([^<]+)<\/title>/)?.[1]);
  assert.equal(meta.get('og:description'), meta.get('description'));
  assert.equal(meta.get('og:image'), imageUrl);
  assert.equal(meta.get('og:image:type'), 'image/png');
  assert.equal(meta.get('twitter:card'), 'summary_large_image');
  assert.equal(meta.get('twitter:title'), meta.get('og:title'));
  assert.equal(meta.get('twitter:description'), meta.get('description'));
  assert.equal(meta.get('twitter:image'), imageUrl);
  assert.ok(meta.get('og:image:alt'));
  assert.equal(meta.get('twitter:image:alt'), meta.get('og:image:alt'));

  const image = readFileSync(resolve('dist', 'social-preview.png'));
  assert.equal(image.toString('hex', 0, 8), '89504e470d0a1a0a');
  assert.equal(Number(meta.get('og:image:width')), image.readUInt32BE(16));
  assert.equal(Number(meta.get('og:image:height')), image.readUInt32BE(20));
});
