// Captures the Finance screenshot set embedded in README.md's Screenshots section.
// Rerun the whole set in one go — the seed is anchored to today, so mixed-vintage shots disagree.
// Owner login, dark theme, 1440x900 @2x, viewport shots, collapsibles opened.
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, relative } from 'node:path';

const BASE = process.env.ODYSSEY_BASE_URL ?? 'http://localhost:5199';
const EMAIL = process.env.ODYSSEY_EMAIL ?? 'owner@demo.example.com';
const PASSWORD = process.env.ODYSSEY_PASSWORD ?? 'Odyssey!Demo1';
// Resolved from this file, not the cwd, so the script runs from anywhere in the repo.
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const OUT = process.env.OUT_DIR ?? join(ROOT, 'docs', 'images');
const only = process.argv.slice(2);

mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({
  baseURL: BASE,
  viewport: { width: 1440, height: 900 },
  deviceScaleFactor: 2,
  colorScheme: 'dark',
  reducedMotion: 'no-preference',
});
const page = await ctx.newPage();

async function login() {
  await page.goto('/login', { waitUntil: 'networkidle' });
  await page.getByLabel('Username or Email').fill(EMAIL);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL((u) => !u.toString().includes('/login'), { timeout: 30000 });
}

async function settle(extra = 1200) {
  await page.waitForLoadState('networkidle').catch(() => {});
  // Let skeletons resolve.
  await page.waitForFunction(() => document.querySelectorAll('.odc-skeleton').length === 0, null,
    { timeout: 20000 }).catch(() => console.warn('  ! skeletons still present'));
  await page.waitForTimeout(extra);
}

// PageHeader region toggles carry their state in the MudButton variant:
// outlined = closed, filled = open. Idempotent because page state persists per user.
async function ensureRegion(label, open = true) {
  const btn = page.locator('button.mud-button-root').filter({ hasText: new RegExp(`^\\s*${label}`, 'i') }).first();
  if (!(await btn.count())) { console.warn(`  ! no "${label}" toggle`); return; }
  const isOpen = await btn.evaluate((e) => e.className.includes('mud-button-filled'));
  if (isOpen !== open) { await btn.click(); await page.waitForTimeout(700); }
}

// Returns the record card, whose toggle label flips Expand -> Collapse once open.
async function ensureExpanded(name) {
  const toggle = new RegExp(`^(Expand|Collapse) ${name}$`, 'i');
  const card = page.locator('.acct-item').filter({ has: page.getByRole('button', { name: toggle }) }).first();
  const btn = card.getByRole('button', { name: toggle }).first();
  await btn.waitFor({ state: 'visible', timeout: 15000 });
  if ((await btn.getAttribute('aria-expanded')) !== 'true') { await btn.click(); }
  await page.waitForTimeout(1500);
  return card;
}

// Position the interesting element near the top of the viewport, leaving `pad` px above.
async function frame(locator, pad = 12) {
  await locator.evaluate((el, p) => {
    const y = el.getBoundingClientRect().top + window.scrollY - p;
    window.scrollTo({ top: Math.max(0, y), behavior: 'instant' });
  }, pad);
  await page.waitForTimeout(600);
}

async function shoot(name) {
  const path = join(OUT, `${name}.png`);
  await page.screenshot({ path });
  console.log(`  -> ${relative(ROOT, path)}`);
}

const shots = {
  async dashboard() {
    await page.goto('/', { waitUntil: 'networkidle' });
    await settle(2500);
    await shoot('dashboard');
  },
  async accounts() {
    await page.goto('/accounts', { waitUntil: 'networkidle' });
    await settle();
    await ensureRegion('Overview', true);
    await ensureRegion('Search', false);
    await settle(2000);
    await shoot('accounts');
  },
  async transactions() {
    await page.goto('/transactions', { waitUntil: 'networkidle' });
    await settle();
    await ensureRegion('Overview', true);
    await ensureRegion('Search', true);
    await settle(1500);
    await shoot('transactions');
  },
  async budgets() {
    await page.goto('/budgets', { waitUntil: 'networkidle' });
    await settle();
    const card = await ensureExpanded(process.env.BUDGET ?? 'Household Budget 2026');
    await settle(1500);
    // The expanded record is ~2.5 viewports tall; frame it from its own header so the shot
    // reads as one budget (identity + period + status) down through both planned donuts.
    await frame(card, 8);
    await shoot('budgets');
  },
  async tax() {
    await page.goto('/tax-statements', { waitUntil: 'networkidle' });
    await settle();
    await ensureRegion('Overview', false);
    const card = await ensureExpanded(process.env.TAXYEAR ?? 'Tax Year 2025');
    await settle(1500);
    await frame(card, 8);
    await shoot('tax-reconciliation');
  },
  async subscriptions() {
    await page.goto('/subscriptions', { waitUntil: 'networkidle' });
    await settle();
    await ensureRegion('Upcoming renewals', true);
    await ensureRegion('Overview', true);
    await ensureRegion('Search', false);
    await settle(1500);
    await shoot('subscriptions');
  },
  async insurance() {
    await page.goto('/insurance-policies', { waitUntil: 'networkidle' });
    await settle();
    await ensureRegion('Renewals', true);
    await ensureRegion('Overview', true);
    await ensureRegion('Search', false);
    await settle(1500);
    await shoot('insurance');
  },
  // Shot 8 of the brief (a transaction expanded to its documents and comments) is
  // deliberately absent: the demo seed attaches no files or comments to transactions, so the
  // panel renders "Files 0" and would contradict the caption it was meant to carry.
};

await login();
console.log(`logged in as ${EMAIL}`);
for (const [name, fn] of Object.entries(shots)) {
  if (only.length && !only.includes(name)) continue;
  console.log(`== ${name}`);
  await fn();
}
await browser.close();
