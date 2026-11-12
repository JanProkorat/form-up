import type { CoachPlanDto } from '@/api/generated';

/** Plan name in the given UI language, falling back to English, Czech, German, then the plan code. */
export function coachPlanName(plan: CoachPlanDto, language: string): string {
  const lang = language.slice(0, 2).toLowerCase();
  const byLang = lang === 'cs' ? plan.nameCs : lang === 'de' ? plan.nameDe : plan.nameEn;
  return byLang || plan.nameEn || plan.nameCs || plan.nameDe || plan.code || '';
}
