# Form Up redesign — design source of truth

Repo snapshot of the **Form Up redesign canvases**, the design source of truth for
the web portal and the mobile apps. Since 2026-11-03 the design lives on three
private claude.ai Design artifacts, one page per module (only the main Claude
session can open them; agents read this snapshot):

| Canvas | Link | Holds |
|---|---|---|
| Web portal | <https://claude.ai/artifact/5GDTsJDGYJ9RevCTva3ciK> | Web portal pages (`Page*`) and the emails (`Email*`) |
| Client app | <https://claude.ai/artifact/KMXvDF1KzZuqkq6LAsTDr9> | Client mobile app (`Glass*`) |
| Coach app | <https://claude.ai/artifact/9ePcFr5gAj5uqzPHzqzXyR> | Coach mobile app (`Coach*`) |

The old combined canvas (<https://claude.ai/artifact/HFzM8WqykBxJvkLdiqU85h>) is
frozen and no longer updated.

**Snapshot of web portal 1794226496-cc4b, client app 1794307123-abc5 and coach app
1794226497-216b, exported 2026-11-10 (#2056).** Partial update on 2026-11-10 (#2065):
`PageAdminCoachProfile`, `PageAdminCoachDialogs` and `PageAdminCoaches` taken from web
portal 1794314120-6fd1 and `PageAdminCoachDrawer` removed; other web boards are still
from 1794226496-cc4b, and PNGs for the new boards are not rendered yet. Partial update on 2026-11-11
(#2070): `PageAdminCoachDialogs` and `PageAdminCoachProfile` updated and `PagePlanChangedNotice`,
`PagePlanEndedNotice` and `PagePlanNotifications` added from web portal 1794403060-8947, also without PNGs.
Partial update on 2026-11-12 (#2080): `PagePlanPicker` updated and `PagePlanDecision` added from web portal
1794472027-2409, without PNGs. Partial update on 2026-11-12 (#2084): `PageClientsRestricted` and
`PageClientDetailRestricted` added from web portal 1794478908-c7e5, without PNGs. Partial update on 2026-11-12
(#2086): `PageClients` and `PageClientsRestricted` updated (tabs by plan cap, plan filter menu) from web portal 1794494910-0948, then `PageLapseKeepClients`
updated and `PageClientsChoicePending` added from web portal 1794497663-42f9, without PNGs. Partial
update on 2026-11-12: `PageAdminCoachProfile` (back button beside the avatar, compact plan card, History scrolling inside its card) from web portal 1794519679-7991. The #2056 export was a full re-sync: boards the
canvases had folded into combined boards (e.g. the `PageReset*` steps into
`PageResetPasswordFlow`, the settings dialogs into `PageDialogs`) are gone, and the
clickable prototypes link to the combined board instead. The concepts and logo boards
are not on the new canvases and are kept as they were. If a canvas has moved on,
re-export before relying on this copy (see Rebuilding).

**Partial update, 2026-11-11 (#2067):** only `PageHomeC` and `PageHomeD` were
re-exported, from web portal 1794399291-7102. Every other board is still from the
versions above.

**Partial update, 2026-11-11 (#2071):** only `GlassRecipeDetail`, `GlassCookingSteps`
(client app 1794404545-f55b) and `PageRecipeDrawerTabs` (web portal 1794404068-898e)
were re-exported, light and dark. `GlassRecipeDetail` is now 1280 px tall; the merged
`canvas.json` keeps the old row positions, so it overlaps the row below there only.

| Path | Contents |
|---|---|
| `index.html` | Gallery of every board, grouped by app and module |
| `interactive/` | Clickable web-portal prototype, light and dark (open `interactive/index.html`) |
| `interactive-client/`, `interactive-coach/` | Clickable client-app and coach-app prototypes, light and dark, shown in a phone frame |
| `web/` | Web portal: entry page, registration, clients (incl. Payment and Automations tabs), inbox, recipes, ingredients, plan templates, forms, automations, packages and discounts, coach profile, settings (incl. payments, plan and billing) and account menu, password reset, coach plans (plan picker, sign-up plan step, paywalls, plan ended), Form Up admin (plans, promotions, coaches) — light and dark |
| `mobile-light/`, `mobile-dark/` | Client mobile app (Liquid Glass) |
| `coach-light/`, `coach-dark/` | Coach mobile app |
| `email/` | Account, notification and payment emails — light (`C`) and dark (`D`) |
| `concepts/`, `logo/` | Colour concept frames and logo explorations |
| `screens.json` | Board list with sizes, used by the screenshot script |
| `source/` | Generators (`gen*.py`), board files (`project/*.dc.html`) and canvas layout (`project/canvas.json`) |

Board files are named after the canvas boards. In `source/project/`, web boards
end in `C` (light) or `D` (dark), e.g. `PageTemplateDayC.dc.html`; mobile boards
end in `Light` / `Dark`. Exported file names add a row-column prefix: web
files keep the theme letter (`web/13-07-page-template-day-c.html`), client-app
files drop the `Glass` prefix, and mobile files drop the theme suffix, which the
folder carries instead (`mobile-dark/02-01-today.html`,
`coach-dark/02-01-coach-today.html`). To find a board reliably by
name, open `source/project/<Board>.dc.html`.

Sample names, numbers and copy are placeholders, not product decisions.

## Clickable prototypes

`interactive/` holds one page per web board and theme. The sidebar, tabs,
switches, drawers, dialogs and the library panel link to the matching board.
`interactive-client/` (starts at Login) and `interactive-coach/` (starts at
coach setup; sign-up is shared with clients) do the same for the mobile apps: the tab bar, back buttons, list
rows and primary buttons link through, and the phone is centred and scaled to
the window.
The floating bar at the bottom right switches light/dark, jumps to any screen
and toggles **Hotspots** (or press `H`) to show what is clickable. Interactions
are approximations: a click jumps to the board that shows the result.

## Rebuilding

```bash
cd source
python3 merge_canvases.py <web> <client> <coach>   # three canvases → project/ (one merged canvas.json)
python3 export_static.py ..          # boards → standalone HTML + index.html + screens.json
python3 export_interactive.py ..     # boards → clickable prototypes in ../interactive*/
node screenshot.mjs ..               # PNG per screen (uses web/node_modules/playwright and Brave)
```

Before exporting, download each canvas's `project/canvas.json` and every board file
into its own folder (`<web>/project/…`, `<client>/project/…`, `<coach>/project/…`) and
run `merge_canvases.py`. It stacks every module page of a canvas under one export page
(`web`, `email`, `glass`, `coach`), replaces the board files in `source/project/` and
removes boards that left the canvases. Clear the generated `.html`/`.png` files in the
export folders first, or renamed boards leave stale copies behind. A board the canvas
folds into another one needs an entry in `MERGED` in `export_interactive.py` so old
prototype links still land somewhere. In each mobile module, light boards sit above
dark ones; the exports split them into `*-light/` and `*-dark/` by the `Light`/`Dark`
file suffix. New board files are gitignored: stage them with `git add -f`. Screenshots use reduced motion, so
animated boards (launch screen, logo marks) show their finished frame. The `gen*.py` scripts regenerate the board files;
`gen.py` and `gen2.py` also rewrite `project/canvas.json` — restore it afterwards.
