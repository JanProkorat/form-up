// Opens the web app as each seeded walk account (from `dotnet run -- --walk-seed`) and reports who the
// app thinks is signed in and whether any /auth/login call was made. Expected: none.
// For plan state use s-features.cjs / s-plancard.cjs with KEY=<key>.
// env KEYS (comma list), default all six seeded keys.
module.exports = async ({ as, BASE, log }) => {
  const keys = (process.env.KEYS || "admin,free,pro,pro3ds,given,manyclients").split(",").filter(Boolean);
  for (const key of keys) {
    const { page } = await as(key);
    const authCalls = [];
    page.on("request", (r) => { if (/\/auth\/(login|register)/.test(r.url())) authCalls.push(r.url()); });
    const me = page.waitForResponse((r) => /\/users\/me$/.test(new URL(r.url()).pathname), { timeout: 15000 }).catch(() => null);
    await page.goto(BASE + (key === "admin" ? "/admin" : "/clients"));
    const meResponse = await me;
    await page.waitForTimeout(2500);
    const body = meResponse && meResponse.ok() ? await meResponse.json() : null;
    log(
      key.padEnd(12),
      "path:", new URL(page.url()).pathname.padEnd(10),
      "me:", body ? `${body.email} [${(body.roles || []).join(",")}]` : "NOT SIGNED IN",
      "login calls:", authCalls.length,
    );
  }
};
