"""Draw the admin Overview board (PageAdminHome{C,D}) and add "Overview" to every admin sidebar.

Usage: python3 -I canvas_admin_home.py <canvas project dir>
Reads PageAdminCoaches{C,D} as the shell, writes PageAdminHome{C,D}, and patches the admin section
of every board whose sidebar has "Form Up admin".
"""
import os
import re
import sys

PROJECT = sys.argv[1]

THEMES = {
    "C": dict(card="#FFFFFF", line="#E7E4DE", ink="#141414", ink2="#3A3835", muted="#6B6863", sunk="#F6F4F0",
              red="#B42318", red_bg="#FDE4E1", amber="#A15C00", amber_bg="#FBEFD9", green="#3F7D2A",
              blue="#3563C9", blue_bg="#E3EBFA", btn_bg="#141414", btn_ink="#FFFFFF"),
    "D": dict(card="#18181A", line="#2A292C", ink="#F4F2EE", ink2="#CFCAC2", muted="#958F87", sunk="#232226",
              red="#F2867D", red_bg="rgba(229,72,61,0.16)", amber="#F0A84B", amber_bg="#3A2C17", green="#8CC152",
              blue="#8FB0F0", blue_bg="rgba(53,99,201,0.22)", btn_bg="#F4F2EE", btn_ink="#141414"),
}

ICON_OVERVIEW = ('<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" '
                 'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="4" y="4" width="7" height="7" rx="1.5">'
                 '</rect><rect x="13" y="4" width="7" height="7" rx="1.5"></rect><rect x="4" y="13" width="7" height="7" rx="1.5">'
                 '</rect><rect x="13" y="13" width="7" height="7" rx="1.5"></rect></svg>')


def icon(paths, size=18):
    return (f'<svg width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" '
            f'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">{paths}</svg>')


CARD_ICON = icon('<rect x="3" y="5.5" width="18" height="13" rx="2"></rect><path d="M3 10h18"></path><path d="M7 15h3"></path>')
CLOCK_ICON = icon('<circle cx="12" cy="12" r="8.5"></circle><path d="M12 7.5V12l3 2"></path>')
GIFT_ICON = icon('<rect x="4" y="9" width="16" height="11" rx="1.5"></rect><path d="M3 9h18"></path><path d="M12 9v11"></path>'
                 '<path d="M12 9c-1.5-3-5-3.5-5-1s3.5 1 5 1c1.5 0 5 1.5 5-1s-3.5-2-5 1z"></path>')
TAG_ICON = icon('<path d="M3.5 12.5V4.5h8l9 9-8 8z"></path><circle cx="8" cy="9" r="1.4"></circle>')
CHEVRON = icon('<path d="M9 6l6 6-6 6"></path>', 16)
SEARCH = icon('<circle cx="11" cy="11" r="6.5"></circle><path d="M16 16l4 4"></path>', 16)
PLUS = icon('<path d="M12 5v14"></path><path d="M5 12h14"></path>', 16)


def main_html(t):
    def attention(chip, chip_bg, ico, title, detail, link):
        return (
            f'<li style="display: flex; align-items: center; gap: 14px; padding: 14px 18px; border-top: 1px solid {t["line"]}">'
            f'<span style="width: 38px; height: 38px; flex-shrink: 0; border-radius: 12px; background: {chip_bg}; color: {chip}; '
            f'display: flex; align-items: center; justify-content: center">{ico}</span>'
            f'<span style="display: flex; flex-direction: column; gap: 3px; min-width: 0; flex-grow: 1">'
            f'<span style="font-size: 14px; font-weight: 600; color: {t["ink"]}">{title}</span>'
            f'<span style="font-size: 13px; color: {t["muted"]}; line-height: 1.45">{detail}</span></span>'
            f'<a href="#" style="flex-shrink: 0; display: inline-flex; align-items: center; gap: 4px; height: 34px; padding: 0 12px; '
            f'border-radius: 10px; border: 1px solid {t["line"]}; background: {t["card"]}; color: {t["ink"]}; text-decoration: none; '
            f'font-size: 13px; font-weight: 600">{link}{CHEVRON}</a></li>')

    def tile(label, value, delta, delta_color, note):
        return (
            f'<div style="border-radius: 16px; border: 1px solid {t["line"]}; background: {t["card"]}; box-shadow: 0 10px 30px rgba(0,0,0,0.05); '
            f'padding: 16px 18px; display: flex; flex-direction: column; gap: 6px; min-width: 0">'
            f'<span style="font-size: 13px; font-weight: 600; color: {t["ink2"]}">{label}</span>'
            f'<span style="display: flex; align-items: baseline; gap: 10px"><span style="font-family: \'Outfit\', sans-serif; font-size: 28px; '
            f'font-weight: 600; letter-spacing: -0.01em; color: {t["ink"]}">{value}</span>'
            + (f'<span style="font-size: 13px; font-weight: 700; color: {delta_color}">{delta}</span>' if delta else '')
            + f'</span>'
            f'<span style="font-size: 12px; color: {t["muted"]}; line-height: 1.45">{note}</span></div>')

    card = (f'border-radius: 16px; border: 1px solid {t["line"]}; background: {t["card"]}; '
            f'box-shadow: 0 10px 30px rgba(0,0,0,0.05)')
    section_title = f'margin: 0; font-family: \'Outfit\', sans-serif; font-size: 18px; font-weight: 600; color: {t["ink"]}'
    rows = "".join([
        attention(t["red"], t["red_bg"], CARD_ICON, "2 payments failed",
                  "Klára Němcová (Pro) and Studio Forma (Studio). Stripe retries for 7 more days.", "Open coaches"),
        attention(t["amber"], t["amber_bg"], CLOCK_ICON, "3 coaches cancelled",
                  "Their paid plans end between 28 Nov and 5 Dec. A short message may keep them.", "Open coaches"),
        attention(t["blue"], t["blue_bg"], GIFT_ICON, "4 free plans you gave end within 14 days",
                  "Pro given free to 4 coaches. Each ends on the date you set; they then choose a plan.", "Open coaches"),
        attention(t["amber"], t["amber_bg"], TAG_ICON, "“Black Friday −30 %” stops on 30 Nov",
                  "46 of 50 uses taken. Coaches can no longer claim it after that date.", "Open promotion"),
    ])
    quick = (
        f'<section aria-labelledby="qa-title" style="{card}; padding: 18px; display: flex; flex-direction: column; gap: 14px">'
        f'<h2 id="qa-title" style="{section_title}">Quick actions</h2>'
        f'<div style="display: flex; flex-direction: column; gap: 7px"><label for="home-search" style="font-size: 13px; font-weight: 600; '
        f'color: {t["ink2"]}">Find a coach</label><span style="position: relative; display: flex; align-items: center">'
        f'<span style="position: absolute; left: 12px; display: flex; color: {t["muted"]}">{SEARCH}</span>'
        f'<input id="home-search" type="text" value="" placeholder="Name or email" style="height: 40px; box-sizing: border-box; '
        f'border-radius: 10px; border: 1px solid {t["line"]}; background: {t["card"]}; color: {t["ink"]}; font: 14px \'DM Sans\', '
        f'sans-serif; width: 100%; padding: 0 12px 0 38px"></span></div>'
        f'<button style="height: 40px; border-radius: 10px; border: none; background: {t["btn_bg"]}; color: {t["btn_ink"]}; '
        f'font: 600 14px \'DM Sans\', sans-serif; display: inline-flex; align-items: center; justify-content: center; gap: 7px">'
        f'{PLUS}New promotion</button></section>')
    tiles = "".join([
        # Every tile shows its change vs the previous 30 days (decision on #2360: backend adds the missing baselines).
        tile("Monthly recurring revenue", "21 860 Kč", "↑ 6 %", t["green"], "Paying coaches, after promotions · vs the 30 days before"),
        tile("New sign-ups", "14", "↑ 3", t["green"], "Coach accounts created in the last 30 days"),
        tile("Paying coaches", "37", "↑ 2", t["green"], "Start 18 · Pro 13 · Studio 6"),
        tile("Churn", "2.7 %", "↓ 0.4 pt", t["green"], "Paying coaches who cancelled in the last 30 days"),
    ])
    return (
        f'<main style="flex-grow: 1; min-width: 0; display: flex; flex-direction: column; background: __BG__; padding: 28px 32px 40px; gap: 22px">'
        f'<div style="display: flex; flex-direction: column; gap: 6px"><h1 style="margin: 0; font-family: \'Outfit\', sans-serif; '
        f'font-size: 30px; font-weight: 600; letter-spacing: -0.01em">Overview</h1>'
        f'<span style="font-size: 14px; color: {t["muted"]}">What needs you, and how Form Up is doing over the last 30 days.</span></div>'
        f'<section aria-labelledby="tm-title" style="display: flex; flex-direction: column; gap: 12px">'
        f'<div style="display: flex; align-items: baseline"><h2 id="tm-title" style="{section_title}">Last 30 days</h2>'
        f'<a href="#" style="margin-left: auto; font-size: 13px; font-weight: 600; color: {t["ink"]}">Full reports</a></div>'
        f'<div style="display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 16px">{tiles}</div></section>'
        f'<div style="display: grid; grid-template-columns: minmax(0, 2fr) minmax(0, 1fr); gap: 16px; align-items: start">'
        f'<section aria-labelledby="na-title" style="{card}; overflow: hidden">'
        f'<div style="display: flex; align-items: baseline; gap: 10px; padding: 16px 18px">'
        f'<h2 id="na-title" style="{section_title}">Needs attention</h2>'
        f'<span style="font-size: 13px; color: {t["muted"]}">4 items</span></div>'
        f'<ul style="list-style: none; margin: 0; padding: 0">{rows}</ul></section>{quick}</div>'
        f'</main>')


def add_overview(nav, active):
    """Insert "Overview" as the first admin item; when active, move the highlight onto it."""
    m = re.search(r'FORM UP ADMIN</div>', nav)
    if not m or ">Overview<" in nav:
        return nav
    anchors = list(re.finditer(r'<a href="#"( aria-current="page")? style="([^"]*)">' + r'<svg\b.*?</svg>' + r'([^<]+)</a>',
                               nav[m.end():], re.S))
    inactive = next(a.group(2) for a in anchors if "background: transparent" in a.group(2))
    current = next((a for a in anchors if "background: transparent" not in a.group(2)), None)
    attrs = ""
    if active and current:
        plain = current.group(0).replace(' aria-current="page"', "").replace(current.group(2), inactive)
        nav = nav[:m.end()] + nav[m.end():].replace(current.group(0), plain, 1)
        attrs = f'{current.group(1) or ""} style="{current.group(2)}"'
    else:
        attrs = f' style="{inactive}"'
    return nav[:m.end()] + f'<a href="#"{attrs}>{ICON_OVERVIEW}Overview</a>' + nav[m.end():]


for theme in "CD":
    t = THEMES[theme]
    src = open(os.path.join(PROJECT, f"PageAdminCoaches{theme}.dc.html")).read()
    shell_end = src.find("<main ")
    bg = re.search(r'<main style="[^"]*?background: (.*?); padding: 28px', src).group(1)
    head = src[:shell_end].replace("<title>Admin · Coaches", "<title>Admin · Overview")
    head = re.sub(r'<nav aria-label="Main".*?</nav>', lambda n: add_overview(n.group(0), True), head, flags=re.S)
    tail = src[src.find("</main>") + len("</main>"):]
    out = head + main_html(t).replace("__BG__", bg) + tail
    out = out.replace("height: 1240px", "height: 900px").replace('"height":1240', '"height":900')
    open(os.path.join(PROJECT, f"PageAdminHome{theme}.dc.html"), "w").write(out)
    print("wrote", f"PageAdminHome{theme}.dc.html")

patched = []
for f in sorted(os.listdir(PROJECT)):
    if not f.startswith("PageAdmin") or f.startswith("PageAdminHome") or not f.endswith(".dc.html"):
        continue
    path = os.path.join(PROJECT, f)
    s = open(path).read()
    new = re.sub(r'<nav aria-label="Main".*?</nav>', lambda n: add_overview(n.group(0), False), s, flags=re.S)
    if new != s:
        open(path, "w").write(new)
        patched.append(f)
print("patched", len(patched), patched)
