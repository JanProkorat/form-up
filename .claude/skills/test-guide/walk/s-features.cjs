// Read GET /users/me/subscription as the given coach session (env KEY), print plan + features + price lines.
module.exports = async ({ as, BASE, log }) => {
  const { page } = await as(process.env.KEY);
  const resp = page.waitForResponse((r) => /\/users\/me\/subscription$/.test(r.url()) && r.request().method() === "GET", { timeout: 20000 });
  await page.goto(BASE + "/settings");
  const r = await resp;
  const j = await r.json();
  const pick = (o, keys) => Object.fromEntries(keys.filter((k) => k in o).map((k) => [k, o[k]]));
  log("TOP:", JSON.stringify(pick(j, ["planCode", "planName", "status", "isSetByFormUp", "givenFreeAt", "freeUntil"])));
  log("FEATURES:", JSON.stringify(j.features));
  log("PLAN BLOCK:", JSON.stringify(j.plan || j.planBlock || {}).slice(0, 600));
};
