import type { CoachPlanDto, CoachPromotionDto } from '@/api/generated';

/** Plan name in the given UI language, falling back to English, Czech, German, then the plan code. */
export function coachPlanName(
  plan: Pick<CoachPlanDto, 'nameCs' | 'nameEn' | 'nameDe' | 'code'>,
  language: string,
): string {
  const lang = language.slice(0, 2).toLowerCase();
  const byLang = lang === 'cs' ? plan.nameCs : lang === 'de' ? plan.nameDe : plan.nameEn;
  return byLang || plan.nameEn || plan.nameCs || plan.nameDe || plan.code || '';
}

/** Promotion name in the given UI language; a missing translation falls back to the Czech name. */
export function promotionDisplayName(
  promotion: Pick<CoachPromotionDto, 'name' | 'nameEn' | 'nameDe'>,
  language: string,
): string {
  const lang = language.slice(0, 2).toLowerCase();
  const byLang = lang === 'en' ? promotion.nameEn : lang === 'de' ? promotion.nameDe : undefined;
  return byLang || promotion.name || '';
}
