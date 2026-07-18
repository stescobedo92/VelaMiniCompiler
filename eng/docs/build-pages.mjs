#!/usr/bin/env node
/**
 * Builds a static documentation site from docs/*.mdx for GitHub Pages.
 * Preserves the Mintlify navigation/theme colors for the official public site.
 */
import { mkdirSync, readFileSync, writeFileSync, cpSync, existsSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { marked } = require('marked');

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const docsRoot = join(root, 'docs');
const outRoot = join(root, 'site');
const config = JSON.parse(readFileSync(join(docsRoot, 'docs.json'), 'utf8'));
const version = readFileSync(join(root, 'VERSION'), 'utf8').trim();

const latest = (config.navigation.versions || []).find((v) => v.default) || config.navigation.versions[0];
const pages = [];
for (const group of latest.groups || []) {
  for (const page of group.pages || []) {
    pages.push({ group: group.group, page });
  }
}

function pagePath(page) {
  return page === 'index' ? 'index.html' : `${page}.html`;
}

function sourcePath(page) {
  const mdx = join(docsRoot, `${page}.mdx`);
  const md = join(docsRoot, `${page}.md`);
  if (existsSync(mdx)) return mdx;
  if (existsSync(md)) return md;
  throw new Error(`Missing page source for ${page}`);
}

function stripMdx(source) {
  let text = source.replace(/^---[\s\S]*?---\s*/m, '');
  text = text.replace(/<CardGroup[\s\S]*?<\/CardGroup>/g, (block) => {
    const cards = [...block.matchAll(/<Card title="([^"]+)"[^>]*href="([^"]+)"[^>]*>\s*([\s\S]*?)\s*<\/Card>/g)];
    if (cards.length === 0) return '';
    return cards
      .map(([, title, href, body]) => `- **[${title}](${href})** — ${body.replace(/\s+/g, ' ').trim()}`)
      .join('\n');
  });
  text = text.replace(/<\/?Note>/g, '\n');
  text = text.replace(/<\/?Warning>/g, '\n');
  text = text.replace(/<\/?[A-Za-z][^>]*>/g, '');
  return text;
}

function hrefFor(fromPage, toPage) {
  const from = pagePath(fromPage).split('/').length - 1;
  const prefix = from > 0 ? '../'.repeat(from) : './';
  return prefix + pagePath(toPage).replace(/\\/g, '/');
}

function renderPage(page, groupName) {
  const source = readFileSync(sourcePath(page), 'utf8');
  const front = source.match(/^---\n([\s\S]*?)\n---/);
  const title = (front?.[1].match(/title:\s*(.+)/)?.[1] || page).replace(/^["']|["']$/g, '');
  const description = (front?.[1].match(/description:\s*(.+)/)?.[1] || '').replace(/^["']|["']$/g, '');
  const htmlBody = marked.parse(stripMdx(source));

  const nav = latest.groups
    .map((group) => {
      const items = group.pages
        .map((p) => {
          const active = p === page ? ' class="active"' : '';
          const label = p === 'index' ? 'Overview' : p.split('/').at(-1).replace(/-/g, ' ');
          return `<li><a href="${hrefFor(page, p)}"${active}>${label}</a></li>`;
        })
        .join('');
      return `<div class="nav-group"><h3>${group.group}</h3><ul>${items}</ul></div>`;
    })
    .join('');

  const depth = pagePath(page).split('/').length - 1;
  const assetPrefix = depth > 0 ? '../'.repeat(depth) : './';

  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>${title} · Vela ${version}</title>
  <meta name="description" content="${description.replace(/"/g, '&quot;')}" />
  <link rel="icon" href="${assetPrefix}favicon.svg" />
  <link rel="preconnect" href="https://fonts.googleapis.com" />
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
  <link href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600;700&family=IBM+Plex+Mono:wght@400;500&display=swap" rel="stylesheet" />
  <link rel="stylesheet" href="${assetPrefix}assets/site.css" />
</head>
<body>
  <div class="shell">
    <aside class="sidebar">
      <a class="brand" href="${hrefFor(page, 'index')}">
        <img src="${assetPrefix}logo/dark.svg" alt="Vela" />
        <span class="badge">${version}</span>
      </a>
      <nav>${nav}</nav>
    </aside>
    <main>
      <header class="top">
        <div>
          <p class="eyebrow">${groupName}</p>
          <h1>${title}</h1>
          ${description ? `<p class="lede">${description}</p>` : ''}
        </div>
        <a class="github" href="https://github.com/stescobedo92/VelaMiniCompiler">GitHub</a>
      </header>
      <article class="content">${htmlBody}</article>
      <footer>
        <p>Official Vela ${version} documentation · Native-first compiler</p>
      </footer>
    </main>
  </div>
</body>
</html>`;
}

mkdirSync(outRoot, { recursive: true });
mkdirSync(join(outRoot, 'assets'), { recursive: true });
writeFileSync(
  join(outRoot, 'assets', 'site.css'),
  ` :root {
  color-scheme: dark;
  --bg: #042f2e;
  --panel: #0b3b38;
  --ink: #f0fdfa;
  --muted: #99b2ae;
  --accent: #2dd4bf;
  --accent-strong: #0f766e;
  --line: rgba(45, 212, 191, 0.22);
  --code: #082f2c;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  font-family: "IBM Plex Sans", "Segoe UI", sans-serif;
  background:
    radial-gradient(900px 420px at 0% 0%, rgba(45,212,191,.16), transparent),
    linear-gradient(180deg, #031f1e, var(--bg));
  color: var(--ink);
}
.shell { display: grid; grid-template-columns: 280px 1fr; min-height: 100vh; }
.sidebar {
  border-right: 1px solid var(--line);
  background: rgba(8, 47, 44, 0.88);
  padding: 1.25rem 1rem 2rem;
  position: sticky; top: 0; height: 100vh; overflow: auto;
}
.brand { display: flex; align-items: center; gap: .75rem; text-decoration: none; color: var(--ink); margin-bottom: 1.5rem; }
.brand img { height: 28px; }
.badge {
  font-size: .72rem; font-weight: 700; color: var(--accent);
  border: 1px solid var(--line); border-radius: 999px; padding: .15rem .5rem;
}
.nav-group + .nav-group { margin-top: 1.25rem; }
.nav-group h3 {
  margin: 0 0 .45rem; font-size: .72rem; letter-spacing: .08em;
  text-transform: uppercase; color: var(--muted);
}
.nav-group ul { list-style: none; margin: 0; padding: 0; }
.nav-group a {
  display: block; text-decoration: none; color: var(--muted);
  padding: .4rem .55rem; border-radius: .55rem; text-transform: capitalize;
}
.nav-group a:hover, .nav-group a.active {
  color: var(--ink); background: rgba(45, 212, 191, 0.12);
}
main { padding: 2rem 2.5rem 3rem; max-width: 920px; }
.top { display: flex; justify-content: space-between; gap: 1rem; align-items: flex-start; margin-bottom: 1.5rem; }
.eyebrow { margin: 0; color: var(--accent); font-weight: 600; font-size: .85rem; }
h1 { margin: .35rem 0 0; font-size: clamp(2rem, 4vw, 2.8rem); letter-spacing: -.03em; }
.lede { color: var(--muted); font-size: 1.1rem; max-width: 40rem; }
.github {
  color: var(--ink); text-decoration: none; border: 1px solid var(--line);
  border-radius: .7rem; padding: .55rem .9rem; font-weight: 600; white-space: nowrap;
}
.content { line-height: 1.7; color: #d7ebe7; }
.content h2, .content h3 { color: var(--ink); margin-top: 2rem; }
.content a { color: var(--accent); }
.content code {
  font-family: "IBM Plex Mono", ui-monospace, monospace;
  background: rgba(45,212,191,.1); padding: .1rem .35rem; border-radius: .35rem;
}
.content pre {
  background: var(--code); border: 1px solid var(--line); border-radius: .9rem;
  padding: 1rem 1.1rem; overflow: auto;
}
.content pre code { background: transparent; padding: 0; }
.content table { width: 100%; border-collapse: collapse; margin: 1rem 0; }
.content th, .content td { border: 1px solid var(--line); padding: .55rem .7rem; text-align: left; }
.content blockquote {
  margin: 1rem 0; padding: .8rem 1rem; border-left: 3px solid var(--accent);
  background: rgba(45,212,191,.08); color: var(--ink);
}
footer { margin-top: 3rem; color: var(--muted); font-size: .9rem; }
@media (max-width: 900px) {
  .shell { grid-template-columns: 1fr; }
  .sidebar { position: relative; height: auto; }
  main { padding: 1.25rem; }
}
`
);

if (existsSync(join(docsRoot, 'favicon.svg'))) {
  cpSync(join(docsRoot, 'favicon.svg'), join(outRoot, 'favicon.svg'));
}
if (existsSync(join(docsRoot, 'logo'))) {
  cpSync(join(docsRoot, 'logo'), join(outRoot, 'logo'), { recursive: true });
}

for (const { page, group } of pages) {
  const target = join(outRoot, pagePath(page));
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, renderPage(page, group));
  console.log('wrote', relative(root, target));
}

writeFileSync(
  join(outRoot, '404.html'),
  `<!doctype html><html><head><meta charset="utf-8"><title>Not found · Vela</title>
  <meta http-equiv="refresh" content="2; url=./index.html"></head>
  <body style="font-family:sans-serif;background:#042f2e;color:#f0fdfa;padding:3rem">
  <h1>Page not found</h1><p><a href="./index.html" style="color:#2dd4bf">Back to docs</a></p></body></html>`
);

console.log(`Built ${pages.length} pages for Vela ${version}`);
