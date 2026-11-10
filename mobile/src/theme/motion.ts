/** Soft colour glows behind the launch screen; centres are fractions of the screen, radii in px. */
export const launchGlowShapes = {
  top: { rx: 420, ry: 360, x: 0.88, y: 0.1 },
  bottom: { rx: 400, ry: 360, x: 0.08, y: 0.94 },
  corner: { rx: 300, ry: 260, x: 0, y: 0 },
} as const;

/** Launch overlay timings in ms; the sequence plays once and the last frame holds. */
export const launchMotion = {
  total: 2244,
  popDuration: 612,
  popStagger: 100,
  barStart: 1020,
  barDuration: 680,
  barStagger: 120,
  tickStart: 1360,
  tickDuration: 510,
  wordmarkStart: 1088,
  wordmarkDuration: 782,
  taglineStart: 1768,
  taglineDuration: 476,
  exitDuration: 250,
} as const;

/** Invite card layout animations on Today: ms for the fades, spring for the entrance and reflow. */
export const inviteMotion = {
  enterDelayStagger: 70,
  exitDuration: 180,
  spring: { damping: 18, stiffness: 180, mass: 0.9 },
} as const;

/** Role invite card entrance on Today: ms to fade in, and the spacing token it slides up from. */
export const roleInviteMotion = {
  duration: 300,
  offsetSpacing: 'xl',
} as const;

/** Drag-to-close on bottom sheets: px before the drag claims the touch, px or velocity (px/ms) that closes it. */
export const sheetDragMotion = {
  claimDistance: 8,
  closeDistance: 110,
  closeVelocity: 0.9,
  spring: { damping: 22, stiffness: 240, mass: 0.9 },
} as const;

/** Request sheet: ms the dimmed backdrop takes to fade in and out while the sheet slides (sheetDragMotion.spring). */
export const requestSheetMotion = {
  backdropDuration: 220,
} as const;

/** Image viewer: ms the controls and the black background take to fade when a tap on the photo toggles them. */
export const imageViewerMotion = {
  fadeDuration: 200,
} as const;

/** Inline date picker in the add-measurement sheet: ms for its fade and for the content below it moving. */
export const measuredAtPickerMotion = {
  enterDuration: 240,
  exitDuration: 160,
  layoutDuration: 240,
} as const;

/** Fade-in of a bottom tab's content when it gains focus, in ms. */
export const tabSwitchMotion = {
  duration: 300,
} as const;

/** Tab bar collapsing to a pill: ms for the morph, and px of scrolling in one direction before it flips. */
export const tabBarCollapseMotion = {
  duration: 240,
  scrollThreshold: 12,
} as const;

/** Chat rows that arrive after the screen opened: spring entrance and list reflow. */
export const chatMotion = {
  spring: { damping: 20, stiffness: 220, mass: 0.8 },
} as const;

/** Progress bar filling to a new value (kcal eaten against target), in ms. */
export const progressFillMotion = {
  duration: 600,
} as const;

/** A long coach note sliding open to its full text or back to its preview lines, in ms. */
export const noteExpandMotion = {
  duration: 240,
} as const;

/** Offline banner sliding down from the top when the app goes offline, and back up when it returns, in ms. */
export const offlineBannerMotion = {
  duration: 240,
} as const;

/** Meal row expanding or collapsing on the plan week view and the Nutrition tab, in ms. */
export const mealExpandMotion = {
  duration: 240,
} as const;

/**
 * Horizontal swipes on the plan week view: px before the pan claims the touch, px of vertical drift that hands it
 * back to scrolling, the distance (px) or velocity (px/s) that commits a swipe, the share of the drag that still
 * follows the finger where there is nothing to swipe to, and the slide-out/slide-in times (ms) and spring-back.
 */
export const swipeMotion = {
  claimDistance: 16,
  verticalTolerance: 12,
  commitDistance: 56,
  commitVelocity: 700,
  resistance: 0.3,
  slideOutDuration: 160,
  slideInDuration: 240,
  spring: { damping: 22, stiffness: 240, mass: 0.9 },
} as const;
