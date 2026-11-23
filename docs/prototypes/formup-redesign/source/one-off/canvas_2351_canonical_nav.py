"""Swap every web board's sidebar, collapsed rail and client header to the canonical v1 shape (#2351, phase 2).

Usage: python3 -I canonical_nav.py <src project dir> <out project dir>
Writes only boards that change; prints a per-board report.
"""
import os
import re
import sys

SRC, OUT = sys.argv[1], sys.argv[2]

TOP = ["Clients", "Inbox", "Check-ins"]
SECTIONS = [("LIBRARY", ["Forms", "Recipes", "Ingredients", "Plan templates"]),
            ("BUSINESS", ["Packages", "Automations"])]
ADMIN = ("FORM UP ADMIN", ["Plans", "Promotions", "Coaches", "Reports"])
ALL_LABELS = TOP + [l for _, ls in SECTIONS for l in ls] + ADMIN[1] + ["Metrics"]
TABS_FULL = ["Overview", "Nutrition", "Photos", "Payment", "Automations", "Forms"]

SVG = r"<svg\b.*?</svg>"
HEADER_START = '<div style="display: flex; align-items: center; gap: 8px; padding: 18px 12px 6px;'
BOTTOM_START = '<div style="margin-top: auto;'

files = sorted(f for f in os.listdir(SRC) if f.endswith(".dc.html"))
texts = {f: open(os.path.join(SRC, f)).read() for f in files}


def full_items(nav):
    """label -> exact <a> html for the full sidebar's middle section."""
    out = {}
    for m in re.finditer(r'<a href="#"[^>]*>(?:(?!</a>).)*?</a>', nav, re.S):
        a = m.group(0)
        # The label is the text right after the icon; badges (unread counts) follow it.
        lm = re.search(r"</svg>\s*([^<]+?)\s*(?:<|$)", a)
        lab = lm.group(1) if lm else ""
        if lab in ALL_LABELS:
            out[lab] = a
    return out


# Icons per label, harvested from any full sidebar (stroke=currentColor, theme-neutral).
ICONS = {}
for f, s in texts.items():
    for nav in re.findall(r'<nav aria-label="Main".*?</nav>', s, re.S):
        for lab, a in full_items(nav).items():
            m = re.search(SVG, a, re.S)
            if m and lab not in ICONS:
                ICONS[lab] = m.group(0)
missing_icons = [l for l in ALL_LABELS if l not in ICONS and l != "Metrics"]
assert not missing_icons, missing_icons
assert all("currentColor" in v for v in ICONS.values())


def is_active(a):
    return "background: transparent" not in a.split(">", 1)[0]


def rebuild_full(nav, f):
    i, j = nav.find(HEADER_START), nav.find(BOTTOM_START)
    if i < 0 or j < 0 or j < i:
        return None, "no section list"
    mid = nav[i:j]
    items = full_items(mid)
    inactive = next((a for a in items.values() if not is_active(a)), None)
    assert inactive, f
    style = re.search(r'style="([^"]*)"', inactive).group(1)
    muted = re.search(r"color: (#[0-9A-Fa-f]{6})", re.search(r'<div style="[^"]*"', mid[mid.find(HEADER_START):]).group(0)).group(1)

    def item(lab):
        if lab in items:
            return items[lab]
        return f'<a href="#" style="{style}">{ICONS[lab]}{lab}</a>'

    def header(t):
        return (f'<div style="display: flex; align-items: center; gap: 8px; padding: 18px 12px 6px; '
                f'font-size: 11px; font-weight: 600; letter-spacing: 0.14em; color: {muted}">{t}</div>')

    parts = [item(l) for l in TOP]
    for title, labs in SECTIONS:
        parts.append(header(title))
        parts += [item(l) for l in labs]
    is_admin = any(l in items for l in ADMIN[1])
    if is_admin:
        parts.append(header(ADMIN[0]))
        parts += [item(l) for l in ADMIN[1]]
    active = [l for l, a in items.items() if is_active(a)]
    lost = [l for l in active if l == "Metrics"]
    assert not lost, (f, "active Metrics")
    return nav[:i] + "".join(parts) + nav[j:], f"full{' +admin' if is_admin else ''} active={active}"


def rebuild_rail(nav, f):
    anchors = list(re.finditer(r'<a href="#" aria-label="([^"]+)"[^>]*>.*?</a>', nav, re.S))
    if not anchors:
        return None, "rail without items"
    items = {m.group(1): m.group(0) for m in anchors}
    unknown = [l for l in items if l not in ALL_LABELS]
    assert not unknown, (f, unknown)
    start, end = anchors[0].start(), anchors[-1].end()
    inactive = next(a for a in items.values() if "background: transparent" in a.split(">", 1)[0])
    style = re.search(r'style="([^"]*)"', inactive).group(1)
    dark = "#121214" in nav[:200]
    div_color = "#2A292C" if dark else "#2A2826"

    def item(lab):
        if lab in items:
            return items[lab]
        return f'<a href="#" aria-label="{lab}" title="{lab}" style="{style}">{ICONS[lab]}</a>'

    divider = f'<span aria-hidden="true" style="width: 24px; height: 1px; margin: 4px 0; background: {div_color}"></span>'
    groups = [TOP] + [labs for _, labs in SECTIONS]
    if any(l in items for l in ADMIN[1]):
        groups.append(ADMIN[1])
    body = divider.join("".join(item(l) for l in g) for g in groups)
    return nav[:start] + body + nav[end:], "rail"


def rebuild_client_header(s, f):
    notes = []
    # Drop the disabled "coming soon" Tasks and Notes buttons.
    s2, n = re.subn(r'<button disabled="" title="Coming soon"[^>]*>' + SVG + r'\s*(?:Tasks|Notes)\s*</button>', "", s, flags=re.S)
    if n:
        notes.append(f"-{n} buttons")
    s = s2

    def fix_tabs(m):
        nav = m.group(0)
        tabs = {re.sub(r"<[^>]+>", "", a).strip(): a for a in re.findall(r"<a\b[^>]*>.*?</a>", nav, re.S)}
        tabs = {("Photos" if k == "Storage" else k): v.replace(">Storage<", ">Photos<") for k, v in tabs.items()}
        inactive = next(a for a in tabs.values() if "aria-current" not in a)
        trainer_only = "Nutrition" not in tabs
        want = [t for t in TABS_FULL if not (trainer_only and t == "Nutrition")]
        built = []
        for t in want:
            built.append(tabs[t] if t in tabs else re.sub(r">[^<>]*</a>$", f">{t}</a>", inactive))
        active = [k for k, a in tabs.items() if "aria-current" in a]
        assert all(a in want for a in active), (f, active)
        head = nav[: nav.find(">") + 1]
        notes.append(f"tabs {list(tabs)} -> {want}")
        return head + "".join(built) + "</nav>"

    s = re.sub(r'<nav aria-label="Client sections".*?</nav>', fix_tabs, s, flags=re.S)
    return s, notes


SKIP = {"ClientHeaderOptionsC.dc.html", "ClientHeaderOptionsD.dc.html"}  # pending the user's delete decision
os.makedirs(OUT, exist_ok=True)
changed = 0
for f in files:
    if f in SKIP:
        continue
    s = texts[f]
    report = []

    def sub_full(m):
        new, why = rebuild_full(m.group(0), f)
        report.append(why)
        return new if new else m.group(0)

    def sub_rail(m):
        new, why = rebuild_rail(m.group(0), f)
        report.append(why)
        return new if new else m.group(0)

    s = re.sub(r'<nav aria-label="Main".*?</nav>', sub_full, s, flags=re.S)
    s = re.sub(r'<nav aria-label="Main navigation, collapsed".*?</nav>', sub_rail, s, flags=re.S)
    if 'aria-label="Client sections"' in s:
        s, notes = rebuild_client_header(s, f)
        report += notes
    if s != texts[f]:
        for tag in ("a", "div", "nav", "button", "span", "svg"):
            assert s.count(f"<{tag}") - s.count(f"</{tag}>") == texts[f].count(f"<{tag}") - texts[f].count(f"</{tag}>"), (f, tag)
        assert ">Metrics<" not in s and 'aria-label="Metrics"' not in s, f
        # Every badge and every highlighted sidebar item must survive the swap.
        for pat in (r'title="\d+ unread[^"]*"', r'background: #2A2826; border: 1px solid #2A2826; color: #F6F4F0',
                    r'background: #232226; border: 1px solid #2A292C; color: #F4F2EE'):
            assert len(re.findall(pat, s)) == len(re.findall(pat, texts[f])), (f, pat)
        open(os.path.join(OUT, f), "w").write(s)
        changed += 1
        print(f, "|", "; ".join(report))
print("changed", changed, "of", len(files))
