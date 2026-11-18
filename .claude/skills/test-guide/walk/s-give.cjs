// Admin gives a plan to a coach (env COACH name, PLAN title, ACTION=change|end). Records busy state and toast.
module.exports = async ({ login, shot, text, BASE, log }) => {
  const coach = process.env.COACH, plan = process.env.PLAN || "Start", action = process.env.ACTION || "change";
  const page = await login("admin");
  await page.goto(BASE + "/admin/coach-accounts");
  await page.waitForTimeout(2500);
  const search = page.getByPlaceholder(/Hledat/i);
  if (await search.count()) { await search.first().fill(coach); await page.waitForTimeout(1500); }
  await page.getByText(coach, { exact: true }).first().click();
  await page.waitForTimeout(3000);
  log("BEFORE:", (await text(page.locator("main"))).split("\n").slice(0, 14).join(" | "));
  const dialog = page.getByRole("dialog");
  if (action === "change") {
    await page.getByRole("button", { name: "Změnit plán" }).first().click();
    await dialog.waitFor();
    await page.waitForTimeout(1200);
    await dialog.getByText(plan, { exact: true }).first().click();
    await dialog.getByLabel("Bez data konce").check();
  } else {
    await page.getByRole("button", { name: "Ukončit plán" }).first().click();
    await dialog.waitFor();
    await page.waitForTimeout(1200);
  }
  log("DIALOG:", (await text(dialog)).split("\n").filter((l) => /Stripe|vrát|role|Role|klient/.test(l)).join(" | "));
  await dialog.locator("textarea").first().fill("Guide walk #592");
  await page.waitForTimeout(500);
  log("BUTTONS:", JSON.stringify(await dialog.locator("button").evaluateAll((bs) => bs.map((b) => ({ t: b.innerText.trim().slice(0, 30), dis: b.disabled, type: b.type })).filter((b) => b.t))));
  log("FOOTER/ERRORS:", (await dialog.locator("[role='alert'], .text-destructive, p").allInnerTexts()).filter((t) => t.trim()).slice(-4).join(" | "));
  await shot(page, "give-before-submit");
  const submit = action === "change" ? dialog.locator('button[type="submit"]') : dialog.getByRole("button", { name: "Ukončit plán hned" }).last();
  log("SUBMIT LABEL:", await submit.innerText());
  await submit.click();
  // Sample the button for a spinner while the request runs.
  let busy = false;
  for (let i = 0; i < 20; i++) {
    const s = await submit.evaluate((b) => !!b.querySelector(".animate-spin") || b.disabled).catch(() => false);
    if (s) { busy = true; break; }
    await page.waitForTimeout(50);
  }
  log("BUSY STATE SEEN:", busy);
  const toast = page.locator("[data-sonner-toast], [role='status'], li[data-type]");
  await toast.first().waitFor({ timeout: 20000 }).catch(() => {});
  log("TOAST:", (await toast.allInnerTexts()).join(" | "));
  await shot(page, "give-" + coach.replace(/\s+/g, "_") + "-" + action);
  await page.waitForTimeout(2500);
  log("AFTER:", (await text(page.locator("main"))).split("\n").slice(0, 14).join(" | "));
};
