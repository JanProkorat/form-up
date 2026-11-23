"""Regroup the Form Up web canvas pages so each screen lives on one page (#2351, phase 1).

Usage: python3 -I regroup.py <in canvas.json> <out canvas.json>
Board files are not touched; only page membership, positions and title notes change.
"""
import json
import sys

src, dst = sys.argv[1], sys.argv[2]
d = json.load(open(src))
boards = d["boards"]

# Pages whose layout stays exactly as it is today.
UNCHANGED = ["webRow1", "webPricing", "webRow2", "webRow11", "webRow12", "webCheckInReview",
             "webRow8", "webRecipes", "webRow6", "webRow7", "webRow13", "webRow9", "webRow19"]

# Final page order and names (sidebar order).
PAGES = [
    ("webStartHere", "Start here"),
    ("webRow1", "Entry page"),
    ("webPricing", "Pricing"),
    ("webRow2", "Registration"),
    ("webRow11", "Forgot password"),
    ("webRow12", "Email turn-off"),
    ("webRow3", "Clients"),
    ("webClientDetail", "Client detail"),
    ("webClientPlan", "Client plan editor"),
    ("webRow4", "Inbox"),
    ("webCheckInReview", "Check-ins"),
    ("webRow8", "Forms"),
    ("webRecipes", "Recipes"),
    ("webRow6", "Ingredients"),
    ("webRow7", "Plan templates"),
    ("webRow13", "Packages & discounts"),
    ("webRow14", "Automations"),
    ("webRow9", "Coach profile"),
    ("webRow10", "Settings"),
    ("webRow17", "Plan & billing"),
    ("webRow19", "Form Up admin"),
    ("emailRow1", "Emails"),
]


def stems_on(page, prefix=""):
    """Light-board stems on an old page, in their current left-to-right order."""
    rows = [(v["x"], k) for k, v in boards.items()
            if v.get("page") == page and k.endswith("C.dc.html") and k.startswith(prefix)]
    return [k[: -len("C.dc.html")] for _, k in sorted(rows)]


def pick(page, names):
    have = set(stems_on(page))
    missing = [n for n in names if n not in have]
    assert not missing, (page, missing)
    return names


clients_page = stems_on("webRow3")
client_plan = [s for s in clients_page if s.startswith("PageClientPlan")]

# New page -> list of (group title, [stems]).
GROUPS = {
    "webStartHere": [
        ("Shared shell — account menu and notifications popovers",
         ["PageAccountMenu", "PageNotificationsPopover", "PageNotificationsOfflineSync"]),
    ],
    "webRow3": [
        ("Clients — list, pending, outside the plan, choice pending, invite drawer",
         pick("webRow3", ["PageClients", "PageClientsPending", "PageClientsRestricted",
                          "PageClientsChoicePending", "PageClientInviteDrawer"])),
    ],
    "webClientDetail": [
        ("Client detail — overview, role variants, outside the plan, recent activity",
         ["PageClientDetail", "PageClientDetailTrainer", "PageClientDetailNutritionist",
          "PageClientDetailRestricted", "PageClientActivityOfflineSync", "ClientHeaderOptions"]),
        ("Nutrition tab", ["PageClientNutrition", "PageClientNutritionEmpty"]),
        ("Photos tab — grid, compare, request photos, viewer",
         ["PageClientPhotos", "PageClientPhotosCompare", "PageClientPhotosRequest", "PageClientPhotosViewer"]),
        ("Payment tab — payments, request payment", ["PageClientPayment", "PageClientPaymentRequest"]),
        ("Automations tab — client rules, weekly check-in changed for one client",
         ["PageClientAutomations", "PageClientAutomationOverrideCheckIn"]),
        ("Forms tab — answers, nothing answered yet, weekly check-in",
         ["PageClientForms", "PageClientFormsEmpty", "PageClientFormsCheckIn"]),
    ],
    "webClientPlan": [
        ("Client plan editor — blank, week, panel closed, day, add meal, plan info, start over",
         [s for s in client_plan if not s.startswith("PageClientPlanForms")]),
        ("Forms panel — client's answers beside the plan",
         ["PageClientPlanForms", "PageClientPlanFormsPopover"]),
        ("Locked meals — eaten and past meals are read-only", stems_on("webMealLock")),
        ("Offline-sync conflicts — week popover and day comparison", stems_on("webOfflineSyncEditor")),
    ],
    "webRow4": [
        ("Inbox — conversations, client invite, new client request",
         [s for s in stems_on("webRow4") if s != "PageNotificationsPopover"]),
        ("Invite review — package chosen, no package, proposal sent, discount, form menu",
         stems_on("webRow5")),
    ],
    "webRow14": [
        ("Automations — rules for all clients, rule editor (schedule, event)", stems_on("webRow14")),
        ("Weekly check-in rule", ["PageAutomationRuleDrawerCheckIn"]),
    ],
    "webRow10": [
        ("Settings — settings page, dialogs (roles, bank, paywall)",
         [s for s in stems_on("webRow10") if s != "PageAccountMenu"]),
        ("Settings — Payments section, change bank account", stems_on("webRow16")),
    ],
    "webRow17": [
        ("Plan & billing — choose a plan, plan and billing states, plan changes", stems_on("webRow17")),
        ("Plan limits — feature not on plan, plan ended: pick clients to keep", stems_on("webRow18")),
    ],
    "emailRow1": [
        ("Account emails — confirm email, password reset, client invitation", stems_on("emailRow1", "Email")),
        ("Notification emails — one per switch in Settings › Notifications", stems_on("emailRow2", "Email")),
        ("Payment emails — payment request with QR Platba code", stems_on("emailRow3", "Email")),
    ],
}

# Every moved board must be placed exactly once, and every board on a rebuilt/removed page must be placed.
placed = [s for gs in GROUPS.values() for _, ss in gs for s in ss]
assert len(placed) == len(set(placed)), "board placed twice"
rebuilt_old_pages = {v["page"] for k, v in boards.items() if v.get("page") not in UNCHANGED}
expected = {k[: -len("C.dc.html")] for k, v in boards.items()
            if v.get("page") in rebuilt_old_pages and k.endswith("C.dc.html")}
assert expected == set(placed), (sorted(expected - set(placed)), sorted(set(placed) - expected))
for s in placed:
    assert s + "C.dc.html" in boards and s + "D.dc.html" in boards, s

GAP_X, GAP_CD, GAP_BLOCK, TITLE_ABOVE = 80, 240, 520, 300
notes = {k: v for k, v in d["notes"].items() if v.get("page") in UNCHANGED}

for page, groups in GROUPS.items():
    top = 0
    for i, (title, stems) in enumerate(groups):
        x, c_h, d_h = 0, 0, 0
        for s in stems:
            c, dk = boards[s + "C.dc.html"], boards[s + "D.dc.html"]
            c.update(page=page, x=x, y=top)
            c_h = max(c_h, c["h"])
            x += max(c["w"], dk["w"]) + GAP_X
        row_w = x - GAP_X
        x = 0
        for s in stems:
            c, dk = boards[s + "C.dc.html"], boards[s + "D.dc.html"]
            dk.update(page=page, x=x, y=top + c_h + GAP_CD)
            d_h = max(d_h, dk["h"])
            x += max(c["w"], dk["w"]) + GAP_X
        nid = page if i == 0 else f"{page}-g{i + 1}"
        notes[nid] = {"kind": "title1", "maxW": min(max(row_w, 1440), 8000), "page": page, "w": 240,
                      "x": 0, "y": top - TITLE_ABOVE, "text": f"{title}  ·  light above, dark below"}
        top += c_h + GAP_CD + d_h + GAP_BLOCK

# Orientation sticky on the Start here page, to the right of the shell boards.
start_w = sum(max(boards[s + "C.dc.html"]["w"], boards[s + "D.dc.html"]["w"]) + GAP_X
              for s in GROUPS["webStartHere"][0][1])
notes["webStartHere-map"] = {
    "page": "webStartHere", "x": start_w + 80, "y": 0, "w": 520, "size": "m", "fill": "gray",
    "text": ("How this canvas is organised\n\n"
             "One page per screen, in the order of the app's sidebar. Every page shows light boards above "
             "and dark boards below; a screen's states sit in labelled rows on its page.\n\n"
             "Public: Entry page · Pricing · Registration · Forgot password · Email turn-off\n"
             "Daily work: Clients · Client detail · Client plan editor · Inbox · Check-ins\n"
             "Library: Forms · Recipes · Ingredients · Plan templates\n"
             "Business: Packages & discounts · Automations\n"
             "Account: Coach profile · Settings · Plan & billing\n"
             "Admin: Form Up admin\n"
             "Emails: Emails"),
}

d["pages"] = [{"id": i, "name": n} for i, n in PAGES]
page_ids = {i for i, _ in PAGES}
assert all(v.get("page") in page_ids for v in boards.values()), "board on a removed page"
d["notes"] = notes
d["noteOrder"] = [i for i, _ in PAGES if i in notes] + sorted(k for k in notes if k not in page_ids)
d["launch"] = {"view": "canvas", "page": "webStartHere"}

json.dump(d, open(dst, "w"), ensure_ascii=False, indent=1)
print("pages", len(d["pages"]), "boards", len(boards), "notes", len(notes), "moved", len(placed) * 2)
