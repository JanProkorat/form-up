// A walk coach buys a plan on Stripe Checkout (sandbox only) with a published Stripe test card.
// Stops if an hCaptcha challenge becomes visible: captchas are never solved or bypassed.
// env KEY (session), PLAN (card title), CARD (test card number)
module.exports = async ({ as, shot, text, BASE, log }) => {
  const { page } = await as(process.env.KEY);
  const plan = process.env.PLAN || "Pro";
  const card = process.env.CARD || "4242424242424242";
  if (!/^(4242424242424242|4000002760003184|4000008400001629)$/.test(card)) throw new Error("Only the guide's published Stripe test cards");
  if (!process.env.SKIP_PICK) {
    await page.goto(BASE + "/plan");
    await page.waitForTimeout(3500);
    if (/login/.test(page.url())) { log("NOT SIGNED IN"); return; }
    await page.getByRole("button", { name: new RegExp("Vybrat " + plan) }).first().click();
    await page.waitForTimeout(800);
    await page.getByRole("button", { name: new RegExp("Přejít na " + plan) }).first().click();
  }
  await page.waitForURL(/checkout\.stripe\.com/, { timeout: 30000 });
  await page.waitForTimeout(5000);
  const sandbox = /Sandbox|test mode|Testovací/i.test(await page.locator("body").innerText());
  log("CHECKOUT host:", new URL(page.url()).host, "SANDBOX:", sandbox);
  if (!sandbox) { log("STOP: not a sandbox checkout"); return; }
  const captchaVisible = async () => {
    for (const el of await page.locator('iframe[src*="hcaptcha"]').all()) {
      const box = await el.boundingBox().catch(() => null);
      if (box && box.width > 50 && box.height > 50 && (await el.isVisible())) return true;
    }
    return false;
  };
  const cur = page.frames().find((f) => /currency-selector/.test(f.url()));
  if (cur) await cur.getByText("CZK", { exact: true }).click().catch((e) => log("CZK click failed", e.message.split("\n")[0]));
  await page.waitForTimeout(1500);
  const pf = page.frames().find((f) => /elements-inner-habanero/.test(f.url()));
  await pf.fill("#payment-numberInput", card);
  await pf.fill("#payment-expiryInput", "12 / 34");
  await pf.fill("#payment-cvcInput", "123");
  await pf.fill("#billingAddress-nameInput", "Walk Test");
  const country = pf.locator("select").first();
  if (await country.count()) await country.selectOption({ label: "Česko" }).catch(() => {});
  await page.waitForTimeout(800);
  for (const [id, v] of [["#billingAddress-addressLine1Input", "Testovací 1"], ["#billingAddress-postalCodeInput", "11000"], ["#billingAddress-localityInput", "Praha"]]) {
    if (await pf.locator(id).count()) await pf.fill(id, v).catch(() => {});
  }
  await pf.locator("#billingAddress-localityInput").press("Escape").catch(() => {});
  await page.mouse.click(300, 700);
  await page.waitForTimeout(800);
  await shot(page, "checkout-filled-" + process.env.KEY);
  let submit = page.locator('button.SubmitButton, button[type="submit"]').filter({ hasText: /Předplatit|Zaplatit|Subscribe|Pay/ });
  if (!(await submit.count())) submit = pf.locator("button").filter({ hasText: /Předplatit|Zaplatit|Subscribe|Pay/ });
  log("SUBMIT:", await submit.count(), await submit.first().innerText().catch(() => "?"));
  await submit.first().scrollIntoViewIfNeeded();
  await submit.first().click();
  const backInApp = () => page.url().startsWith(BASE); // BASE follows WALK_BASE
  for (let i = 0; i < 40; i++) {
    if (backInApp()) break;
    if (await captchaVisible()) { log("STOP: hCaptcha challenge visible"); await shot(page, "captcha-" + process.env.KEY); return; }
    for (const f of page.frames()) {
      const btn = f.locator('#test-source-authorize-3ds, button:has-text("Complete"), button:has-text("Dokončit"), button:has-text("COMPLETE")');
      if (await btn.count().catch(() => 0)) { log("3DS test page: pressing Complete"); await btn.first().click().catch(() => {}); }
    }
    await page.waitForTimeout(1500);
  }
  log("BACK AT:", page.url());
  if (!backInApp()) { log("CHECKOUT TEXT:", (await page.locator("body").innerText()).slice(0, 400)); await shot(page, "checkout-stuck-" + process.env.KEY); return; }
  await page.waitForTimeout(12000);
  log("APP:", (await text(page.locator("body"))).slice(0, 500).replace(/\n+/g, " | "));
  await shot(page, "after-buy-" + process.env.KEY);
};
