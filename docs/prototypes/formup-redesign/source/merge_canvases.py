"""Merge the three Form Up canvases (web, client app, coach app) into project/ for the exporters.

Usage: python3 merge_canvases.py <web_dir> <client_dir> <coach_dir>
Each <dir> holds project/canvas.json and project/*.dc.html downloaded from one canvas.
Pass "-" for the client or coach dir to keep that app's boards unchanged.
The exporters expect one canvas with the pages web, glass, coach and email, so every module page
of a canvas is stacked under its app page, row title 300 px above its row. The concepts and logo
pages are not on the new canvases; their boards and notes are kept from the current project/.
"""
import json, os, shutil, sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.join(HERE, "project")
GAP = 600  # space between one module's last row and the next module's title

web_dir, client_dir, coach_dir = sys.argv[1:4]
old = json.load(open(os.path.join(PROJECT, "canvas.json")))
KEEP = {"concepts", "logo"}
# "-" for the client or coach dir keeps that app's boards as they are (re-sync one canvas only).
KEEP |= {page for page, d in (("glass", client_dir), ("coach", coach_dir)) if d == "-"}

out = {
    "v": 3,
    "title": "Form Up redesign (merged from the web, client-app and coach-app canvases)",
    "pages": [{"id": "concepts", "name": "Concepts"}, {"id": "web", "name": "Web portal"},
              {"id": "glass", "name": "Client app"}, {"id": "coach", "name": "Coach app"},
              {"id": "logo", "name": "Logo & icon"}, {"id": "email", "name": "Emails"}],
    "boards": {k: b for k, b in old["boards"].items() if b.get("page") in KEEP},
    "notes": {k: n for k, n in old["notes"].items() if n.get("page") in KEEP},
    "order": [k for k in old["order"] if old["boards"].get(k, {}).get("page") in KEEP],
}
sources = {k: os.path.join(PROJECT, k) for k in out["boards"]}
offset = {}  # parent page -> next free y


def parent(canvas_dir, page_id):
    if canvas_dir == client_dir:
        return "glass"
    if canvas_dir == coach_dir:
        return "coach"
    return "email" if page_id.startswith("email") else "web"


for d in (web_dir, client_dir, coach_dir):
    if d == "-":
        continue
    c = json.load(open(os.path.join(d, "project", "canvas.json")))
    for page in c["pages"]:
        pid, target = page["id"], parent(d, page["id"])
        titles = {k: n for k, n in c["notes"].items() if n.get("page") == pid and n.get("kind") == "title1"}
        note = titles.get(pid) or next(iter(titles.values()))
        boards = {k: b for k, b in c["boards"].items() if b.get("page") == pid}
        top = min(b["y"] for b in boards.values())  # the module's first row
        y0 = offset.get(target, 0)
        # export_static.py finds a row's title 300 px above the row, so pin it there
        out["notes"][pid] = dict(note, page=target, x=0, y=y0 - 300)
        # A page with several labelled rows keeps each row's title, moved with its row.
        for k, n in titles.items():
            if n is not note:
                out["notes"][k] = dict(n, page=target, x=0, y=y0 + n["y"] - top)
        for k, b in boards.items():
            out["boards"][k] = dict(b, page=target, y=y0 + b["y"] - top)
            sources[k] = os.path.join(d, "project", k)
        bottom = max(b["y"] + b["h"] for b in boards.values()) - top
        offset[target] = y0 + bottom + GAP
    out["order"] += [k for k in c["order"] if k in c["boards"]]

# replace the board files: drop boards that left the canvases, copy the current ones.
# Files the old index never listed (old logo explorations) are left alone.
staging = {k: open(p).read() for k, p in sources.items()}
removed = sorted(k for k in old["boards"] if k not in staging and os.path.exists(os.path.join(PROJECT, k)))
for f in removed:
    os.remove(os.path.join(PROJECT, f))
for k, text in staging.items():
    with open(os.path.join(PROJECT, k), "w") as f:
        f.write(text)
with open(os.path.join(PROJECT, "canvas.json"), "w") as f:
    json.dump(out, f, ensure_ascii=False, indent=1)
count = {}
for b in out["boards"].values():
    count[b["page"]] = count.get(b["page"], 0) + 1
print("boards per page:", count, "| removed board files:", len(removed))
