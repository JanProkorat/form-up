// Guide-walk driver: runs one step script against the dev web app in headless Brave.
// Usage: node walk.cjs <step-script.cjs> [width] [theme]
const path = require("path");
const fs = require("fs");
// WALK_PLAYWRIGHT loads Playwright from another checkout's web/node_modules.
const { chromium } = require(process.env.WALK_PLAYWRIGHT || "/Users/jan/Projects/form-up/web/node_modules/playwright");

const BRAVE = "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser";
const BASE = process.env.WALK_BASE || "http://localhost:5173"; // WALK_BASE points the walk at a branch web server
const ACCOUNTS = process.env.WALK_ACCOUNTS || "/Users/jan/Projects/form-up/.claude/private/walk-accounts.json";
const SHOTS = process.env.WALK_SHOTS || `/Users/jan/Projects/form-up/.qa-artifacts/${process.env.EPIC || "walk"}/walk`;
const STATE = process.env.WALK_STATE || "/Users/jan/Projects/form-up/.qa-artifacts/walk-sessions"; // gitignored; holds signed-in sessions

(async () => {
  const [, , scriptPath, width = "1440", theme = "light"] = process.argv;
  fs.mkdirSync(SHOTS, { recursive: true });
  fs.mkdirSync(STATE, { recursive: true });
  const accounts = JSON.parse(fs.readFileSync(ACCOUNTS, "utf8"));
  const browser = await chromium.launch({ executablePath: BRAVE, headless: true });
  const contexts = {};
  const consoleErrors = [];
  const failed = [];

  // One browser context per account, restored from saved storage when present.
  async function as(who) {
    if (contexts[who]) return contexts[who];
    const stateFile = path.join(STATE, who.replace(/[^a-z0-9]/gi, "_") + ".json");
    const ctx = await browser.newContext({
      viewport: { width: +width, height: 900 },
      colorScheme: theme,
      locale: "cs-CZ",
      ...(fs.existsSync(stateFile) ? { storageState: stateFile } : {}),
    });
    const page = await ctx.newPage();
    page.on("console", (m) => { if (m.type() === "error") consoleErrors.push(`[${who}] ${m.text()}`); });
    page.on("response", (r) => { if (r.status() >= 400 && r.status() !== 401) failed.push(`[${who}] ${r.status()} ${r.request().method()} ${r.url()}`); });
    contexts[who] = { ctx, page, stateFile };
    return contexts[who];
  }

  async function login(who) {
    const acc = who === "admin" ? accounts.admin : accounts.coaches.find((c) => c.email === who || c.key === who);
    if (!acc) throw new Error("No walk account for " + who);
    const { page, ctx, stateFile } = await as(who);
    // Probe a page the account may open: coach-only /clients answers an admin with 403s.
    await page.goto(BASE + (acc === accounts.admin ? "/admin" : "/clients"));
    await page.waitForTimeout(1500);
    if (!/\/login|\/$/.test(new URL(page.url()).pathname) && !(await page.locator('input[type="password"]').count())) return page;
    await page.goto(BASE + "/login");
    await page.locator('input[type="email"], input[name="email"]').first().fill(acc.email);
    await page.locator('input[type="password"]').first().fill(acc.password);
    await page.locator('form button[type="submit"]').first().click();
    await page.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: 15000 });
    await ctx.storageState({ path: stateFile });
    return page;
  }

  const shot = async (page, name, opts = {}) => {
    const file = path.join(SHOTS, `${name}-${width}-${theme}.png`);
    await page.screenshot({ path: file, fullPage: false, ...opts });
    console.log("SHOT", file);
    return file;
  };
  // Visible text of the page or a locator, squashed, for reading results.
  const text = async (target) => (await target.innerText()).replace(/\n{2,}/g, "\n").slice(0, 4000);

  try {
    const step = require(path.resolve(scriptPath));
    await step({ as, login, shot, text, BASE, width: +width, theme, log: (...a) => console.log(...a) });
  } catch (e) {
    console.log("STEP ERROR", e && e.message ? e.message.split("\n")[0] : e);
  } finally {
    for (const { ctx, stateFile } of Object.values(contexts)) { try { await ctx.storageState({ path: stateFile }); } catch {} }
    if (consoleErrors.length) console.log("CONSOLE ERRORS\n" + [...new Set(consoleErrors)].slice(0, 10).join("\n"));
    if (failed.length) console.log("HTTP ERRORS\n" + failed.join("\n"));
    await browser.close();
  }
})();
