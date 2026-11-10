/** Board blur values by role, plus the expo-blur intensity that renders them. */
export type GlassBlur = {
  /** Blur radius in px. */
  blur: number;
  /** Saturation multiplier (1 = unchanged). */
  saturate: number;
  /** expo-blur intensity (0–100); bar was tuned in the simulator, the others scale linearly with `blur`. */
  intensity: number;
};

/** Bottom sheet over a dimmed screen. */
export type GlassSheet = {
  /** Backdrop fill per colour scheme. */
  backdrop: { light: string; dark: string };
  /** Radius of the two top corners. */
  radius: number;
  handleWidth: number;
  handleHeight: number;
};

/** Full-screen image viewer. */
export type GlassViewer = {
  /** Background once the controls are hidden: the photo alone on black, in both colour schemes. */
  immersive: string;
};

export type Glass = {
  viewer: GlassViewer;
  /** Tab bars, headers and sheets. */
  bar: GlassBlur;
  /** Round controls floating over imagery, such as an overlay back button. */
  control: GlassBlur;
  /** Small badges over media, such as a play button. */
  badge: GlassBlur;
  sheet: GlassSheet;
};

export const glass: Glass = {
  viewer: { immersive: '#000000' },
  bar: { blur: 22, saturate: 1.8, intensity: 50 },
  control: { blur: 18, saturate: 1, intensity: 41 },
  badge: { blur: 16, saturate: 1, intensity: 36 },
  sheet: {
    backdrop: { light: 'rgba(0,0,0,0.32)', dark: 'rgba(0,0,0,0.55)' },
    radius: 26,
    handleWidth: 36,
    handleHeight: 5,
  },
};
