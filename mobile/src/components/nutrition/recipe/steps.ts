import type { RecipeStepDto } from '@/api/generated';

export const MIN_STEP_MINUTES = 1;
export const MAX_STEP_MINUTES = 180;

/** A preparation step as the screens use it; a step is shown by its text, so `description` may be empty only when there is a title. */
export type RecipeStep = {
  title?: string;
  description: string;
  minutes?: number;
};

const validMinutes = (value: unknown): number | undefined =>
  typeof value === 'number' && Number.isInteger(value) && value >= MIN_STEP_MINUTES && value <= MAX_STEP_MINUTES
    ? value
    : undefined;

const trimmed = (value: unknown): string => (typeof value === 'string' ? value.trim() : '');

const normalizeStep = (raw: unknown): RecipeStep | undefined => {
  if (typeof raw === 'string') {
    const description = raw.trim();
    return description.length > 0 ? { description } : undefined;
  }
  if (typeof raw !== 'object' || raw === null) return undefined;

  const step = raw as Partial<RecipeStepDto>;
  const title = trimmed(step.title);
  const description = trimmed(step.description);
  if (title.length === 0 && description.length === 0) return undefined;

  const minutes = validMinutes(step.minutes);
  return {
    ...(title.length > 0 ? { title } : {}),
    description,
    ...(minutes !== undefined ? { minutes } : {}),
  };
};

/**
 * The steps of a recipe, safe to render. Recipe data is persisted on the phone, so a cache written before steps
 * became objects can still hold plain strings; those become description-only steps. Blank steps are dropped.
 */
export function normalizeSteps(raw: readonly (RecipeStepDto | string)[] | null | undefined): RecipeStep[] {
  if (!Array.isArray(raw)) return [];
  return raw.flatMap((entry) => {
    const step = normalizeStep(entry);
    return step ? [step] : [];
  });
}

/** Cook mode needs more than one step to be worth opening. */
export const canCook = (steps: readonly RecipeStep[]): boolean => steps.length >= 2;
