#!/usr/bin/env node
// Playwright driver for the running Odyssey stack (nginx → Blazor WASM → API → MariaDB).
//
// The app is a web app: a Blazor WebAssembly SPA on http://localhost:5199 talking to the
// ASP.NET API on http://localhost:5188, cookie-authenticated. This driver logs in as a seeded
// demo user and drives/screenshots authed pages so an agent can SEE the running app.
//
// Usage:
//   node driver.mjs health                 # no browser: probe API /healthz + client root
//   node driver.mjs smoke                  # login, assert seeded data, shoot login+accounts+/
//   node driver.mjs shot <route> [name]    # login, navigate to <route>, screenshot it
//
// Env (all optional; defaults target the Docker Compose dev stack + deterministic demo seed):
//   ODYSSEY_BASE_URL   default http://localhost:5199   (the client / SPA origin)
//   ODYSSEY_API_URL    default http://localhost:5188   (the API origin, for `health`)
//   ODYSSEY_EMAIL      default admin@demo.example.com   (seeded demo Admin)
//   ODYSSEY_PASSWORD   default Odyssey!Demo1
//   HEADLESS           default 1   (set 0 only if you have a display)
//
// Screenshots land in .claude/skills/run-odyssey/screenshots/ (gitignored).

import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const SHOTS = join(HERE, 'screenshots');
const BASE = process.env.ODYSSEY_BASE_URL ?? 'http://localhost:5199';
const API = process.env.ODYSSEY_API_URL ?? 'http://localhost:5188';
const EMAIL = process.env.ODYSSEY_EMAIL ?? 'admin@demo.example.com';
const PASSWORD = process.env.ODYSSEY_PASSWORD ?? 'Odyssey!Demo1';
const HEADLESS = (process.env.HEADLESS ?? '1') !== '0';

async function health() {
  // The API exposes its version + status on /healthz; the client root should serve index.html.
  const h = await fetch(`${API}/healthz`).catch((e) => ({ ok: false, _err: e.message }));
  const body = h.ok ? await h.text() : (h._err ?? `status ${h.status}`);
  console.log(`API  ${API}/healthz -> ${h.ok ? 'OK' : 'FAIL'}: ${String(body).slice(0, 200)}`);
  const c = await fetch(BASE).catch((e) => ({ ok: false, _err: e.message }));
  console.log(`SPA  ${BASE}/ -> ${c.ok ? `OK (${c.status})` : `FAIL: ${c._err ?? c.status}`}`);
  if (!h.ok || !c.ok) process.exit(1);
}

async function withLogin(fn) {
  mkdirSync(SHOTS, { recursive: true });
  const browser = await chromium.launch({ headless: HEADLESS });
  const ctx = await browser.newContext({ baseURL: BASE, ignoreHTTPSErrors: true, viewport: { width: 1366, height: 900 } });
  const page = await ctx.newPage();
  try {
    await page.goto('/login', { waitUntil: 'networkidle' });
    await page.getByLabel('Username or Email').fill(EMAIL);
    await page.getByLabel('Password').fill(PASSWORD);
    await page.getByRole('button', { name: 'Sign in' }).click();
    // Login navigates away from /login on success.
    await page.waitForURL((u) => !u.toString().includes('/login'), { timeout: 30_000 });
    await fn(page);
  } finally {
    await browser.close();
  }
}

async function shoot(page, route, name) {
  await page.goto(route, { waitUntil: 'networkidle' });
  await page.waitForTimeout(800); // let MudBlazor render/animate in
  const out = join(SHOTS, `${name}.png`);
  await page.screenshot({ path: out, fullPage: true });
  console.log(`shot ${route} -> ${out}`);
  return out;
}

async function smoke() {
  await withLogin(async (page) => {
    console.log(`logged in as ${EMAIL}; landed on ${page.url()}`);
    await shoot(page, '/', 'dashboard');
    await page.goto('/accounts', { waitUntil: 'networkidle' });
    // Seeing a seeded account proves the whole chain: cookie auth + SPA + API + demo seed.
    const acct = page.getByText('Everyday Checking').first();
    await acct.waitFor({ state: 'visible', timeout: 30_000 });
    console.log('seeded account "Everyday Checking" is visible — full stack OK');
    await page.waitForTimeout(800);
    await page.screenshot({ path: join(SHOTS, 'accounts.png'), fullPage: true });
    console.log(`shot /accounts -> ${join(SHOTS, 'accounts.png')}`);
  });
}

const [cmd, route, name] = process.argv.slice(2);
if (cmd === 'health') await health();
else if (cmd === 'smoke') await smoke();
else if (cmd === 'shot') {
  if (!route) { console.error('usage: node driver.mjs shot <route> [name]'); process.exit(2); }
  const slug = name ?? (route.replace(/\W+/g, '_').replace(/^_|_$/g, '') || 'shot');
  await withLogin((page) => shoot(page, route, slug));
} else {
  console.error('usage: node driver.mjs <health|smoke|shot <route> [name]>');
  process.exit(2);
}
