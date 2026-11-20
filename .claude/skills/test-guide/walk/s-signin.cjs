// Sign in the walk coaches named in env KEYS (comma list) and save their sessions.
module.exports = async ({ login, log }) => {
  for (const key of (process.env.KEYS || "").split(",").filter(Boolean)) {
    try { const p = await login(key); log(key, "signed in at", p.url()); }
    catch (e) { log(key, "sign-in failed:", e.message.split("\n")[0]); }
  }
};
