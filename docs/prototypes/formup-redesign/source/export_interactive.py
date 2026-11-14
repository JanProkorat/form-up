"""Build the clickable prototypes from the canvas boards.

Usage: python3 export_interactive.py <out_dir>
Writes <out_dir>/interactive/ (web portal), interactive-client/ and interactive-coach/ (mobile apps):
one HTML per board and theme, wired with a click map, a light/dark switch and a screen picker.
Press H in the browser to show the hotspots.
"""
import html, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
canvas = json.load(open(os.path.join(HERE, "project", "canvas.json")))

# Navigation shared by every page with the sidebar or the collapsed rail.
NAV = {"Clients": "PageClients", "Inbox": "PageInbox", "Recipes": "PageRecipes", "Ingredients": "PageIngredients",
       "Plan templates": "PageTemplates", "Forms": "PageForms", "Automations": "PageAutomations", "Packages": "PagePackages",
       "Log out": "PageHome", "Martin Král": "PageAccountMenu"}

# Per page: visible text, aria-label or title of the clicked element -> target board.
# A key starting with ^ matches the beginning of the text, * matches anywhere in it.
FLOWS = {
    "PageHome": {"Sign in": "PageHomeSignIn", "Create account": "PageRegister"},
    "PageHomeSignIn": {"Close": "PageHome", "Sign in": "PageClients", "Create account": "PageRegister", "Forgot password?": "PageResetRequest"},
    "PageResetRequest": {"Send reset link": "PageResetCheckEmail", "Back to sign in": "PageHomeSignIn"},
    "PageResetCheckEmail": {"^The link works": "PageResetNewPassword", "Send again": "PageResetCheckEmail", "Back to sign in": "PageHomeSignIn"},
    "PageResetNewPassword": {"Save new password": "PageResetDone"},
    "PageResetDone": {"Sign in": "PageHomeSignIn"},
    "PageResetExpired": {"Send a new link": "PageResetRequest", "Back to sign in": "PageHomeSignIn"},
    "PageAccountMenu": {"Account menu": "PageClients", "Your profile": "PageProfile", "Settings": "PageSettings"},
    "PageProfile": {"Settings": "PageSettings", "Packages": "PagePackages"},
    "PageSettings": {"Change password": "PageSettingsPassword", "Disable account": "PageSettingsDisable", "Remove role": "PageSettingsRemoveTrainer"},
    "PageSettingsPassword": {"Close": "PageSettings", "Cancel": "PageSettings", "Change password": "PageSettings",
                             "Forgot your current password?": "PageResetRequest"},
    "PageSettingsDisable": {"Close": "PageSettings", "Keep my account": "PageSettings", "Disable account": "PageSettings"},
    "PageSettingsRemoveTrainer": {"Keep role": "PageSettings", "^Remove Personal": "PageSettings"},
    "PageSettingsRemoveNutrition": {"Keep role": "PageSettings", "^Remove Nutritionist": "PageSettings"},
    "PageSettingsRolesOne": {"Disable account": "PageSettingsDisable", "Remove role": "PageSettingsDisable"},
    "PageRegister": {"Create account": "PageRegisterCheckEmail", "Sign in": "PageHomeSignIn"},
    "PageRegisterCheckEmail": {"^Open Gmail": "PageRegisterVerified", "^Open Outlook": "PageRegisterVerified", "Change email": "PageRegister"},
    "PageRegisterVerified": {"^Set up your profile": "PageClients", "Skip for now and go to the portal": "PageClients"},
    "PageClients": {"Eva Svobodová": "PageClientDetail", "^Invite client": "PageClientInviteDrawer"},
    "PageClientInviteDrawer": {"Close": "PageClients", "^Cancel": "PageClients", "^Send invitation": "PageClients"},
    "PageClientDetail": {"Nutrition": "PageClientNutrition", "Payment": "PageClientPayment", "Automations": "PageClientAutomations",
                         "Back to clients": "PageClients", "^Open plan": "PageClientPlanEditor", "^Chat": "PageInbox"},
    "PageClientPayment": {"Overview": "PageClientDetail", "Nutrition": "PageClientNutrition", "Automations": "PageClientAutomations",
                          "Automations tab": "PageClientAutomations", "Back to clients": "PageClients", "^Request payment": "PageClientPaymentRequest",
                          "^Chat": "PageInbox"},
    "PageClientPaymentRequest": {"Close": "PageClientPayment", "^Cancel": "PageClientPayment", "^Send request": "PageClientPayment"},
    "PageClientAutomations": {"Overview": "PageClientDetail", "Nutrition": "PageClientNutrition", "Payment": "PageClientPayment",
                              "Back to clients": "PageClients", "Open": "PageFormCheckIn", "^Chat": "PageInbox"},
    "PagePackages": {"Discounts": "PageDiscounts", "^New package": "PagePackageDrawer", "Online coaching": "PagePackageDrawer", "^Open profile": "PageProfile"},
    "PagePackageDrawer": {"Close": "PagePackages", "^Cancel": "PagePackages", "^Save package": "PagePackages"},
    "PageDiscounts": {"Packages": "PagePackages", "^New discount": "PageDiscountDrawer", "Loyal client": "PageDiscountDrawer"},
    "PageDiscountDrawer": {"Close": "PageDiscounts", "^Cancel": "PageDiscounts", "^Save discount": "PageDiscounts"},
    "PageAutomations": {"^New rule": "PageAutomationRuleDrawer", "Progress photos": "PageAutomationRuleDrawer",
                        "End-of-plan review": "PageAutomationRuleEventDrawer", "Open": "PageFormCheckIn"},
    "PageAutomationRuleDrawer": {"Close": "PageAutomations", "^Cancel": "PageAutomations", "^Save rule": "PageAutomations",
                                 "^After an event": "PageAutomationRuleEventDrawer"},
    "PageAutomationRuleEventDrawer": {"Close": "PageAutomations", "^Cancel": "PageAutomations", "^Save rule": "PageAutomations",
                                      "^On a schedule": "PageAutomationRuleDrawer"},
    "PageSettingsPayments": {"Change account": "PageSettingsBank", "^Open Automations": "PageAutomations", "^Open Packages": "PagePackages"},
    "PageSettingsBank": {"Close": "PageSettingsPayments", "Cancel": "PageSettingsPayments", "Change account": "PageSettingsPayments"},
    "PageClientNutrition": {"Overview": "PageClientDetail", "Back to clients": "PageClients", "^Open editor": "PageClientPlanEditor", "^Chat": "PageInbox"},
    "PageClientNutritionEmpty": {"Overview": "PageClientDetail", "Back to clients": "PageClients", "^Use “": "PageClientPlanEditor", "^Start blank": "PageClientPlanBlank", "^Chat": "PageInbox"},
    "PageClientPlanEditor": {"Use": "PageClientPlanStartOver", "Plan Info": "PageClientPlanInfo", "Library": "PageClientPlanClosed", "Close panel": "PageClientPlanClosed",
                             "Day": "PageClientPlanDay", "Back to Eva": "PageClientNutrition"},
    "PageClientPlanStartOver": {"^Cancel": "PageClientPlanEditor", "Replace draft weeks": "PageClientPlanEditor"},
    "PageClientPlanInfo": {"Library": "PageClientPlanEditor", "Plan Info": "PageClientPlanClosed", "Close panel": "PageClientPlanClosed",
                           "Day": "PageClientPlanDay", "Back to Eva": "PageClientNutrition"},
    "PageClientPlanClosed": {"Open panel": "PageClientPlanEditor", "Library": "PageClientPlanEditor", "Plan Info": "PageClientPlanInfo",
                             "Day": "PageClientPlanDay", "Back to Eva": "PageClientNutrition"},
    "PageClientPlanDay": {"Week": "PageClientPlanEditor", "^Add meal": "PageClientPlanDayAddMeal", "Plan Info": "PageClientPlanInfo",
                          "Library": "PageClientPlanClosed", "Close panel": "PageClientPlanClosed", "Back to Eva": "PageClientNutrition"},
    "PageClientPlanDayAddMeal": {"^Cancel": "PageClientPlanDay", "Close": "PageClientPlanDay", "^Add meal": "PageClientPlanDay"},
    "PageClientPlanBlank": {"Use": "PageClientPlanEditor", "Plan Info": "PageClientPlanInfo", "Back to Eva": "PageClientNutrition"},
    "PageRecipes": {"Table": "PageRecipesTable", "^New recipe": "PageRecipeDrawer", "*Chicken rice bowl": "PageRecipeDrawer"},
    "PageRecipesTable": {"Cards": "PageRecipes", "^New recipe": "PageRecipeDrawer", "*Chicken rice bowl": "PageRecipeDrawer"},
    "PageRecipeDrawer": {"^Ingredients ·": "PageRecipeDrawerIngredients", "^Preparation ·": "PageRecipeDrawerPreparation", "^Pictures ·": "PageRecipeDrawerPictures",
                         "Close": "PageRecipes", "^Cancel": "PageRecipes", "^Save Recipe": "PageRecipes"},
    "PageIngredients": {"^New Ingredient": "PageIngredientDrawer", "*Red lentils": "PageIngredientDrawer"},
    "PageIngredientDrawer": {"Close": "PageIngredients", "^Cancel": "PageIngredients", "^Save Ingredient": "PageIngredients"},
    "PageTemplates": {"^New template": "PageTemplateNew", "^Cut — 4 weeks": "PageTemplateEditor", "^Lean bulk": "PageTemplateEditor",
                      "^Maintenance": "PageTemplateEditor", "^Vegetarian cut": "PageTemplateEditor"},
    "PageTemplateNew": {"Close": "PageTemplates", "^Cancel": "PageTemplates", "^Create & open editor": "PageTemplateEditorEmpty"},
    "PageTemplateEditorEmpty": {"^Breakfast · Snack": "PageTemplateEditor"},
    "PageTemplateEditor": {"Day": "PageTemplateDay", "Nutrition": "PageTemplateNutrition", "Collapse library": "PageTemplateNutrition", "^Chicken rice bowl": "PageTemplateMeal"},
    "PageTemplateMeal": {"Close": "PageTemplateEditor", "Day": "PageTemplateDay", "Nutrition": "PageTemplateNutrition"},
    "PageTemplateNutrition": {"Meals": "PageTemplateEditor", "Day": "PageTemplateDay", "Open library": "PageTemplateEditor"},
    "PageTemplateDay": {"Week": "PageTemplateEditor", "Nutrition": "PageTemplateNutrition", "^Add meal": "PageTemplateDayAddMeal"},
    "PageTemplateDayAddMeal": {"^Cancel": "PageTemplateDay", "Close": "PageTemplateDay", "^Add meal": "PageTemplateDay"},
    "PageForms": {"Client Onboarding Form": "PageFormOnboarding", "Weekly Check-In": "PageFormCheckIn", "^New form": "PageFormOnboarding"},
    "PageFormOnboarding": {"Back to forms": "PageForms"},
    "PageFormCheckIn": {"Back to forms": "PageForms"},
}
# The recipe drawer's other tabs share the Details tab's map, plus a way back to Details.
for tab in ("PageRecipeDrawerIngredients", "PageRecipeDrawerPreparation", "PageRecipeDrawerPictures"):
    FLOWS[tab] = dict(FLOWS["PageRecipeDrawer"], Details="PageRecipeDrawer")


def prefixed(prefix, flows):
    """Write mobile maps with short board names; this adds the Glass/Coach prefix to pages and targets."""
    return {prefix + page: {k: (prefix + t if t else None) for k, t in fl.items()} for page, fl in flows.items()}


# Client app (Glass boards). The tab bar badge renders as "1Messages", hence the * key.
CLIENT_NAV = {"Today": "GlassToday", "Training": "GlassTrainingWeek", "Nutrition": "GlassNutrition", "*Messages": "GlassMessagesList"}
_today = {"Notifications": "Notifications", "Profile": "Profile", "Add measurement": "Measurements", "Take progress photo": "Photos",
          "Fill in check-in": "CheckIn", "^Lower body A": "SessionDetail", "Start Lower body A": "Workout", "Mobility flow": "SessionDetail",
          "^2 meals eaten": "Nutrition", "^Chicken rice bowl": "MealDetail", "^Salmon, potatoes": "MealDetail"}
_no_coach = {"Today": "TodayNoCoach", "Training": "TrainingEmpty", "Nutrition": "NutritionEmpty"}
_open_ing = {"^Open ": "IngredientDetail", "^How to make it": "CookingSteps"}
CLIENT_FLOWS = prefixed("Glass", {
    "Login": {"Sign in": "Today", "Apple": "Today", "Google": "Today", "Create account": "Register"},
    "Register": {"Back": "Login", "Create account": "RegisterCheckEmail"},
    "RegisterCheckEmail": {"Back": "Register", "Open email app": "RegisterVerified", "Change email": "RegisterChangeEmail"},
    "RegisterChangeEmail": {"Back": "Register", "Cancel": "RegisterCheckEmail", "Send new link": "RegisterCheckEmail"},
    "RegisterVerified": {"Set up your profile": "OnboardingGoal"},
    "OnboardingGoal": {"Back": "RegisterVerified", "Continue": "OnboardingBasics"},
    "OnboardingBasics": {"Back": "OnboardingGoal", "Continue": "OnboardingLifestyle"},
    "OnboardingLifestyle": {"Back": "OnboardingBasics", "Continue": "OnboardingTraining"},
    "OnboardingTraining": {"Back": "OnboardingLifestyle", "Continue": "OnboardingEating"},
    "OnboardingEating": {"Back": "OnboardingTraining", "Continue": "OnboardingMotivation", "*Add allergy": "OnboardingEatingSheet",
                         "*Add food": "OnboardingEatingSheet"},
    "OnboardingEatingSheet": {"Done": "OnboardingEating", "Back": "OnboardingTraining", "Continue": "OnboardingMotivation"},
    "OnboardingMotivation": {"Back": "OnboardingEating", "Finish": "TodayNoCoach"},
    "TodayNoCoach": dict(_no_coach, **{"Notifications": "Notifications", "^Trainer": "CoachSearch", "^Nutritionist": "CoachSearch",
                                       "Accept": "Today"}),
    "TrainingEmpty": dict(_no_coach, **{"Find a trainer": "CoachSearch"}),
    "NutritionEmpty": dict(_no_coach, **{"Find a nutritionist": "CoachSearch"}),
    "CoachSearch": {"Back": "TodayNoCoach", "^MK ": "CoachProfile", "^TD ": "CoachProfile", "^PV ": "CoachProfile", "^LH ": "CoachProfile"},
    "CoachProfile": {"Back": "CoachSearch", "Send request": "RequestSheet"},
    "RequestSheet": {"Send request": "TodayNoCoach"},
    "Today": _today,
    "TodayV1": dict(_today, **{"^Weekly check-in": "CheckIn"}),
    "Notifications": dict(_today, **{"Notifications": "Today", "Mark all read": "Today", "Take photos": "Photos"}),
    "TrainingWeek": {"Full plan": "TrainingPlan", "Workouts": "WorkoutsPlan", "^Mon": "SessionDetail", "^Wed": "SessionDetail",
                     "^Today": "SessionDetail", "^Sat": "SessionMixed", "Exercise library": "WorkoutsAll"},
    "SessionDetail": {"Back": "TrainingWeek", "Start workout": "Workout", "^1. ": "ExerciseDetail", "^2. ": "ExerciseDetail", "^3. ": "ExerciseDetail",
                      "^4. ": "ExerciseDetail", "^5. ": "ExerciseDetail", "^6. ": "ExerciseDetail"},
    "SessionMixed": {"Back": "TrainingWeek", "Start workout": "Workout"},
    "Workout": {"Back": "SessionDetail", "Log set 3": "DoneStrength"},
    "DoneStrength": {"Start AMRAP": "Amrap"},
    "Amrap": {"Round done": "DoneAmrap", "End workout": "DoneAmrap"},
    "DoneAmrap": {"Start Tabata": "Tabata"},
    "Tabata": {"Skip interval": "DoneTabata", "End workout": "DoneTabata"},
    "DoneTabata": {"See session summary": "SessionSummary"},
    "Emom": {"Done": "SessionSummary", "End workout": "SessionSummary"},
    "ForTime": {"^Finish": "SessionSummary", "End workout": "SessionSummary"},
    "SessionSummary": {"Finish": "Today"},
    "Nutrition": {"Full plan": "NutritionPlan", "Recipes": "RecipesPlan", "Shopping list": "ShoppingList",
                  "^Oat bowl with berries": "RecipeDetail", "^Chicken rice bowl": "RecipeDetail", "^Apple": "IngredientDetail",
                  "Open breakfast photo 1 of 2": "MealPhotoViewer", "Open breakfast photo 2 of 2": "MealPhotoViewer",
                  "^Add food you ate outside the plan": "NutritionOffPlanSearch"},
    "NutritionOffPlan": {"Full plan": "NutritionPlan", "Recipes": "RecipesPlan", "Shopping list": "ShoppingList",
                         "^Banana": "IngredientDetail", "^Latte": "IngredientDetail",
                         "^Add food you ate outside the plan": "NutritionOffPlanSearch"},
    "NutritionOffPlanSearch": {"Close": "Nutrition", "^Banana": "NutritionOffPlanAmount"},
    "NutritionOffPlanAmount": {"Back to search": "NutritionOffPlanSearch", "Close": "Nutrition", "Add to today": "NutritionOffPlan"},
    "MealPhotoViewer": {"Close": "Nutrition"},
    "MealDetail": dict(_open_ing, **{"Back": "Nutrition", "Log meal": "Nutrition", "Swap meal": "RecipesPlan"}),
    "ShoppingList": {"Back": "Nutrition"},
    "MessagesList": {"New message": "Chat", "^MKMartin": "Chat", "^JKJana": "Chat", "^PVPetra": "Chat"},
    "Chat": {"Back": "MessagesList", "Coach details": "CoachProfile", "^New week published": "TrainingPlan"},
    "Profile": {"Back": "Today", "My journey": "MyJourney", "^Body measurements": "Measurements", "^Progress photos": "Photos",
                "^Check-in history": "CheckIn", "Settings": "Settings", "^MKMartin": "Chat", "^JKJana": "Chat"},
    "MyJourney": {"Back": "Profile", "Photos": "Photos", "^Strength block": "PlanSummary", "^Cut — phase 2": "NutritionPlan"},
    "PlanSummary": {"Back": "MyJourney", "^All sessions": "TrainingPlan"},
    "Measurements": {"Back": "Profile"},
    "CheckIn": {"Back": "Today", "Close": "Today", "Send to Martin": "Today"},
    "Settings": {"Back": "Profile", "Log out": "Login", "Messages": None},
    "Payment": {"Back": "Profile"},
    "TrainingPlan": {"Back": "TrainingWeek", "^Weeks 1": "TrainingWeekDetail", "^Week 5": "TrainingWeekDetail",
                     "^Week 6": "TrainingWeekDetail", "^Week 7": "TrainingWeekDetail"},
    "TrainingWeekDetail": {"Back": "TrainingPlan", "^Upper body A": "SessionDetail", "^Lower body A": "SessionDetail",
                           "^Mobility flow": "SessionDetail", "^Easy run": "SessionDetail", "^Full body C": "SessionMixed"},
    "NutritionPlan": {"Back": "Nutrition", "^Weeks 1": "NutritionPlanWeek", "^Week 7": "NutritionPlanWeek", "^Week 8": "NutritionPlanWeek",
                      "^Week 9": "NutritionPlanWeek", "^Shopping list": "ShoppingList"},
    "NutritionPlanWeek": {"Back": "NutritionPlan", "^Oat bowl with berries": "RecipeDetail", "^Chicken rice bowl": "RecipeDetail",
                          "^Apple": "IngredientDetail", "Open breakfast photo 1 of 2": "MealPhotoViewer",
                          "Open breakfast photo 2 of 2": "MealPhotoViewer"},
    "RecipesPlan": {"Back": "Nutrition", "All plans": "RecipesAll", "*kcal": "RecipeDetail"},
    "RecipesAll": {"Back": "Nutrition", "This plan": "RecipesPlan", "*kcal": "RecipeDetail"},
    "RecipeDetail": dict(_open_ing, Back="RecipesPlan"),
    "CookingSteps": {"Back to recipe": "RecipeDetail", "^Done": "RecipeDetail"},
    "IngredientDetail": {"Back to recipe": "RecipeDetail"},
    "WorkoutsPlan": {"Back": "TrainingWeek", "All plans": "WorkoutsAll", "*exercise": "WorkoutDetail"},
    "WorkoutsAll": {"Back": "TrainingWeek", "This plan": "WorkoutsPlan", "*exercise": "WorkoutDetail"},
    "WorkoutDetail": {"Back": "WorkoutsPlan", "^Open ": "ExerciseDetail", "^In your plan": "SessionMixed"},
    "ExerciseDetail": {"Back to workout": "WorkoutDetail"},
})

# Coach app (Coach boards).
COACH_NAV = {"Today": "CoachToday", "Clients": "CoachClients", "*Messages": "CoachMessages"}
_coach_today = {"Notifications": "Notifications", "Profile": "Profile", "Invite client": "Invite", "^Eva Svobodová": "CheckInReview",
                "^Tomáš Dvořák": "ClientDetail", "^Lucie Horáková": "JoinRequest", "Accept": "Clients", "^Petr Novotný": "ClientDetail",
                "^Kateřina Veselá": "ClientPlans", "+ 2 more": "Clients"}
_roster = {n: "ClientDetail" for n in ("^Eva Svobodová", "^Tomáš Dvořák", "^Kateřina Veselá", "^Petr Novotný", "^Jana Kučerová", "^Ondřej Marek")}
COACH_FLOWS = prefixed("Coach", {
    "Setup": {"Back": None, "Later": "Today", "Finish": "Today"},
    "Today": _coach_today,
    "Notifications": dict(_coach_today, **{"Notifications": "Today", "Mark all read": "Today", "Review": "CheckInReview"}),
    "Clients": dict(_roster, **{"Find clients": "FindClients", "Invite client": "Invite", "Search": "ClientsSearch"}),
    "ClientsSearch": {"Close search": "Clients", "^Eva Svobodová": "ClientDetail", "^Evžen Kos": "ClientDetail"},
    "FindClients": {"My clients": "Clients", "Invite client": "Invite", "Search": "ClientsSearch",
                    "^LH ": "Prospect", "^MS ": "Prospect", "^BN ": "Prospect", "^DK ": "Prospect"},
    "Invite": {"Send invitation": "Clients", "Share invite link": "Clients"},
    "Prospect": {"Back": "FindClients", "Send coaching offer": "FindClients"},
    "JoinRequest": {"Back": "Today", "Decline": "Today", "Accept": "Clients"},
    "ClientDetail": {"Back": "Clients", "Plans": "ClientPlans", "Check-ins": "CheckInReview", "Message": "Messages", "^Last check-in": "CheckInReview"},
    "ClientPlans": {"Back": "Clients", "Overview": "ClientDetail", "Check-ins": "CheckInReview"},
    "CheckInReview": {"Back": "ClientDetail", "Mark reviewed & reply": "ClientDetail"},
    "Messages": {"Broadcast": "Broadcast"},
    "Broadcast": {"Close": "Messages", "Send to 5 clients": "Messages"},
    "Profile": {"Back": "Today"},
})

RUNTIME = r"""
<style>
html,body{margin:0}
body{overflow-x:hidden}
body.fu-device{min-height:100vh;display:flex;align-items:center;justify-content:center;background:#E4E1DC}
body.fu-device.fu-dark{background:#1C1C1F}
.fu-hot{cursor:pointer}
body.fu-show .fu-hot{outline:2px solid rgba(37,99,235,.75);outline-offset:1px}
#fu-bar{position:fixed;right:16px;bottom:16px;z-index:9999;display:flex;gap:6px;align-items:center;padding:6px;border-radius:14px;background:rgba(20,20,20,.88);color:#fff;font:600 12px system-ui,sans-serif;box-shadow:0 10px 30px rgba(0,0,0,.3)}
#fu-bar a,#fu-bar button,#fu-bar select{height:30px;padding:0 10px;border-radius:9px;border:0;background:rgba(255,255,255,.12);color:#fff;font:inherit;text-decoration:none;display:flex;align-items:center;cursor:pointer}
#fu-bar .on{background:#fff;color:#141414}
</style>
<script>
(function(){
  var FLOW = __FLOW__, PAGE = __PAGE__, THEME = __THEME__, PAGES = __PAGES__, DEVICE = __DEVICE__;
  function key(el){ return (el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent || '').replace(/\s+/g,' ').trim(); }
  function target(el){
    // Exact keys win over ^prefix and *contains keys; a null target marks a label as not clickable.
    var k = key(el), m;
    if (Object.prototype.hasOwnProperty.call(FLOW, k)) return FLOW[k];
    for (m in FLOW){ var c = m[0], v = m.slice(1); if (c === '^' ? k.indexOf(v) === 0 : c === '*' ? k.indexOf(v) >= 0 : false) return FLOW[m]; }
    return null;
  }
  function go(page, theme){ location.href = page + '-' + (theme || THEME) + '.html'; }
  function fit(){
    // Boards are fixed-size frames. Web: zoom to the window width, page in the board's colour.
    // Phone: centre the frame, zoomed to fit the window, on a neutral backdrop.
    var board = document.body.firstElementChild;
    if (!board || board.id === 'fu-bar') return;
    if (!board.dataset.w){
      board.dataset.w = board.offsetWidth; board.dataset.h = board.offsetHeight;
      if (DEVICE){
        document.body.classList.add('fu-device'); if (THEME === 'dark') document.body.classList.add('fu-dark');
        board.style.borderRadius = '48px'; board.style.boxShadow = '0 30px 80px rgba(0,0,0,.28), 0 0 0 10px #0B0B0C';
      } else {
        document.body.style.background = getComputedStyle(board).backgroundColor;
      }
    }
    var root = document.documentElement;
    board.style.zoom = DEVICE ? Math.min((root.clientHeight - 64) / board.dataset.h, (root.clientWidth - 64) / board.dataset.w)
                              : root.clientWidth / board.dataset.w;
  }
  window.addEventListener('resize', fit);
  document.addEventListener('DOMContentLoaded', function(){
    fit();
    document.querySelectorAll('a,button,tr,[role=tab],[role=radio]').forEach(function(el){ if (target(el)) el.classList.add('fu-hot'); });
    var bar = document.createElement('div'); bar.id = 'fu-bar';
    var opts = PAGES.map(function(p){ return '<option value="'+p[0]+'"'+(p[0]===PAGE?' selected':'')+'>'+p[1]+'</option>'; }).join('');
    bar.innerHTML = '<select aria-label="Jump to screen">'+opts+'</select>'
      + '<button data-t="light" class="'+(THEME==='light'?'on':'')+'">Light</button><button data-t="dark" class="'+(THEME==='dark'?'on':'')+'">Dark</button>'
      + '<button data-h="1" title="Show clickable areas (H)">Hotspots</button><a href="../index.html">All screens</a>';
    document.body.appendChild(bar);
    bar.querySelector('select').addEventListener('change', function(e){ go(e.target.value); });
    bar.querySelectorAll('[data-t]').forEach(function(b){ b.addEventListener('click', function(){ go(PAGE, b.getAttribute('data-t')); }); });
    bar.querySelector('[data-h]').addEventListener('click', function(){ document.body.classList.toggle('fu-show'); });
  });
  document.addEventListener('keydown', function(e){ if (e.key === 'h' || e.key === 'H') document.body.classList.toggle('fu-show'); });
  document.addEventListener('click', function(e){
    if (e.target.closest && e.target.closest('#fu-bar')) return;
    var el = e.target.closest ? e.target.closest('a,button,tr,[role=tab],[role=radio]') : null;
    if (!el) return;
    e.preventDefault();
    var t = target(el);
    if (t) go(t); else if (el.tagName !== 'TR' && !document.body.classList.contains('fu-show')) { document.body.classList.add('fu-show'); setTimeout(function(){ document.body.classList.remove('fu-show'); }, 600); }
  }, true);
})();
</script>
"""


# Boards the canvas later folded into one combined board; links to them open the combined board.
MERGED = {
    **dict.fromkeys(["PageRecipeDrawer", "PageRecipeDrawerIngredients", "PageRecipeDrawerPreparation", "PageRecipeDrawerPictures"],
                    "PageRecipeDrawerTabs"),
    **dict.fromkeys(["PageRegisterCheckEmail", "PageRegisterVerified", "PageRegisterChangeEmail"], "PageRegisterEmailSteps"),
    **dict.fromkeys(["PageResetRequest", "PageResetCheckEmail", "PageResetNewPassword", "PageResetDone", "PageResetExpired"],
                    "PageResetPasswordFlow"),
    **dict.fromkeys(["PageSettingsBank", "PageSettingsDisable", "PageSettingsPassword", "PageSettingsRemoveTrainer",
                     "PageSettingsRemoveNutrition", "PageSettingsRolesOne", "PageSettingsAddRole"], "PageDialogs"),
    **dict.fromkeys(["GlassNutritionEmpty", "GlassTrainingEmpty"], "GlassEmptyStates"),
    **dict.fromkeys(["GlassNutritionOffPlanSearch", "GlassNutritionOffPlanAmount"], "GlassNutritionAddFood"),
    **dict.fromkeys(["GlassRegisterCheckEmail", "GlassRegisterVerified", "GlassRegisterChangeEmail"], "GlassRegisterEmailSteps"),
    "PageAdminPlanDrawer": "PageAdminPlanEditor",
}

# Output folder -> canvas pages (light, dark), board-name suffix pattern, shared nav, per-page maps, start page, phone frame.
ZONES = [
    ("interactive", ("web",), r"[CD]\.dc\.html$", NAV, FLOWS, "PageHome", False),
    ("interactive-client", ("glass",), r"(Light|Dark)\.dc\.html$", CLIENT_NAV, CLIENT_FLOWS, "GlassLogin", True),
    ("interactive-coach", ("coach",), r"(Light|Dark)\.dc\.html$", COACH_NAV, COACH_FLOWS, "CoachSetup", True),
]


def build(folder, pages, suffix, nav, flows, start, device):
    out = os.path.join(sys.argv[1], folder)
    boards = sorted(((k, b) for k, b in canvas["boards"].items() if b.get("page") in pages), key=lambda kb: (kb[1]["y"], kb[1]["x"]))
    names = []
    for k, b in boards:
        n = re.sub(suffix, "", k)
        if n not in [x[0] for x in names]:
            names.append((n, re.sub(r"^[CD] · ", "", b.get("title", n))))
    os.makedirs(out, exist_ok=True)
    for k, b in boards:
        name, theme = re.sub(suffix, "", k), ("dark" if re.search(r"(D|Dark)\.dc\.html$", k) else "light")
        src = open(os.path.join(HERE, "project", k)).read()
        helmet = re.search(r"<helmet>(.*?)</helmet>", src, re.S).group(1).strip()
        body = re.search(r"</helmet>(.*?)</x-dc>", src, re.S).group(1).strip()
        flow = {k: MERGED.get(t, t) for k, t in dict(nav, **flows.get(name, {})).items()}
        runtime = (RUNTIME.replace("__FLOW__", json.dumps(flow, ensure_ascii=False)).replace("__PAGE__", json.dumps(name))
                   .replace("__THEME__", json.dumps(theme)).replace("__PAGES__", json.dumps(names, ensure_ascii=False))
                   .replace("__DEVICE__", json.dumps(device)))
        title = re.sub(r"^[CD] · ", "", b.get("title", name))
        with open(os.path.join(out, f"{name}-{theme}.html"), "w") as f:
            f.write(f'<!doctype html>\n<html lang="en">\n<head>\n<meta charset="utf-8">\n<meta name="viewport" content="width=device-width, initial-scale=1">\n'
                    f'<title>{html.escape(title)} · Form Up prototype</title>\n{helmet}\n{runtime}\n</head>\n<body>\n{body}\n</body>\n</html>\n')
    with open(os.path.join(out, "index.html"), "w") as f:
        f.write(f'<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Form Up prototype</title>'
                f'<meta http-equiv="refresh" content="0; url={start}-light.html"></head><body><a href="{start}-light.html">Open the prototype</a></body></html>\n')
    known = {n for n, _ in names}
    missing = sorted({MERGED.get(t, t) for fl in list(flows.values()) + [nav] for t in fl.values() if t} - known)
    print(f"{folder}: {len(boards)} screens; unknown targets: {missing}")


for zone in ZONES:
    build(*zone)
