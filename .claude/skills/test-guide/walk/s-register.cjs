// Register a walk coach (role from env ROLE, key from env KEY) and record it in walk-accounts.json.
module.exports = async ({ as, shot, text, BASE, log }) => {
  const fs = require("fs");
  const crypto = require("crypto");
  const FILE = "/Users/jan/Projects/form-up/.claude/private/walk-accounts.json";
  const key = process.env.KEY;
  const role = process.env.ROLE || "Osobní trenér";
  const accounts = JSON.parse(fs.readFileSync(FILE, "utf8"));
  if (accounts.coaches.find((c) => c.key === key)) { log("exists", key); return; }
  const email = `walk.592.${key}.${Date.now() % 100000}@formup.test`;
  const password = "Walk-" + crypto.randomBytes(9).toString("base64url") + "1a";
  const { page, ctx, stateFile } = await as(key);
  await page.goto(BASE + "/register");
  await page.waitForTimeout(2000);
  await page.getByRole("button", { name: new RegExp(role) }).first().click();
  await page.fill("#entry-firstName", "Walk");
  await page.fill("#entry-lastName", key.toUpperCase());
  await page.fill("#entry-register-email", email);
  await page.fill("#entry-register-password", password);
  await page.click("#entry-gdpr-consent");
  if (await page.locator("#entry-health-consent").isVisible().catch(() => false)) await page.click("#entry-health-consent");
  await page.getByRole("button", { name: "Založit účet" }).click();
  await page.waitForTimeout(5000);
  accounts.coaches.push({ key, email, password, note: `walk #592 coach ${key}, ${role}` });
  fs.writeFileSync(FILE, JSON.stringify(accounts, null, 2));
  await ctx.storageState({ path: stateFile });
  log("registered", key, email, "landed", page.url());
  log((await text(page.locator("body"))).slice(0, 600));
  await shot(page, "register-" + key);
};
