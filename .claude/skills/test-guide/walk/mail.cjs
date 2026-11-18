// Print the newest verify-email link sent to the given address, from the local mail catcher.
(async () => {
  const to = process.argv[2];
  const tryJson = async (u) => { try { const r = await fetch(u); return r.ok ? await r.json() : null; } catch { return null; } };
  let body = null;
  const hog = await tryJson("http://localhost:8025/api/v2/search?kind=to&query=" + encodeURIComponent(to));
  if (hog && hog.items && hog.items.length) {
    body = hog.items[0].Content.Body;
    console.log("source mailhog, messages", hog.items.length);
  } else {
    const pit = await tryJson("http://localhost:8025/api/v1/search?query=" + encodeURIComponent("to:" + to));
    if (pit && pit.messages && pit.messages.length) {
      const m = await tryJson("http://localhost:8025/api/v1/message/" + pit.messages[0].ID);
      body = m && (m.HTML || m.Text);
      console.log("source mailpit");
    }
  }
  if (!body) { console.log("NO MAIL for", to); return; }
  const flat = body.replace(/=\r?\n/g, "").replace(/=3D/g, "=").replace(/&amp;/g, "&");
  const links = [...new Set(flat.match(/https?:\/\/[^\s"'<>]+verify-email[^\s"'<>]*/g) || [])];
  console.log(links.join("\n"));
})();
