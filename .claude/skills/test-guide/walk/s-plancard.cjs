// Read a coach's own plan card in Settings (env KEY = session).
module.exports = async ({ as, shot, BASE, log }) => {
  const { page } = await as(process.env.KEY);
  await page.goto(BASE + "/settings");
  await page.waitForTimeout(4000);
  if (/login/.test(page.url())) { log("NOT SIGNED IN"); return; }
  const heading = page.getByRole("heading", { name: /Tarif a platby|Tarif/ }).first();
  await heading.scrollIntoViewIfNeeded().catch(() => {});
  const section = heading.locator("xpath=ancestor::section[1]");
  const t = (await section.count()) ? await section.innerText() : "(section not found)";
  log("PLAN CARD:", t.replace(/\n+/g, " | ").slice(0, 600));
  log("HAS ZRUŠENO:", /Zrušeno/.test(t));
  await shot(page, "plancard-" + process.env.KEY);
};
