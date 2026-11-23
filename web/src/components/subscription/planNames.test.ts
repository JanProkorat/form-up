import { describe, expect, it } from 'vitest';
import { promotionDisplayName } from '@/components/subscription/planNames';

const promotion = { name: 'Úvodní nabídka', nameEn: 'Launch offer', nameDe: 'Startangebot' };

describe('promotionDisplayName', () => {
  it('picks the name for the UI language', () => {
    expect(promotionDisplayName(promotion, 'cs')).toBe('Úvodní nabídka');
    expect(promotionDisplayName(promotion, 'en')).toBe('Launch offer');
    expect(promotionDisplayName(promotion, 'de')).toBe('Startangebot');
  });

  it('reads a regional code by its first two letters', () => {
    expect(promotionDisplayName(promotion, 'en-US')).toBe('Launch offer');
    expect(promotionDisplayName(promotion, 'DE-at')).toBe('Startangebot');
  });

  it('falls back to the Czech name when the translation is missing or empty', () => {
    expect(promotionDisplayName({ name: 'Úvodní nabídka' }, 'en')).toBe('Úvodní nabídka');
    expect(promotionDisplayName({ ...promotion, nameDe: '' }, 'de')).toBe('Úvodní nabídka');
  });

  it('uses the Czech name for any other language', () => {
    expect(promotionDisplayName(promotion, 'fr')).toBe('Úvodní nabídka');
  });
});
