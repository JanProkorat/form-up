// Open verification links in the signed-in sessions. env PAIRS="key=url;key=url"
module.exports = async ({ as, BASE, log }) => {
  for (const pair of (process.env.PAIRS || "").split(";").filter(Boolean)) {
    const [key, url] = [pair.slice(0, pair.indexOf("=")), pair.slice(pair.indexOf("=") + 1)];
    const { page } = await as(key);
    await page.goto(url);
    await page.waitForTimeout(4000);
    log(key, "verify page:", (await page.locator("body").innerText()).split("\n").filter((l) => /ověřen|neplatný|Neplatný/i.test(l)).join(" | "));
    await page.goto(BASE + "/plan");
    await page.waitForTimeout(3000);
    log(key, "now at", page.url());
  }
};
