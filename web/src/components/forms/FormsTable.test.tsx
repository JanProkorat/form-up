import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { QuestionnaireType, type QuestionnaireSummaryDto } from '@/api/questionnaires';
import { renderWithProviders } from '@/test/render';
import FormsTable from '@/components/forms/FormsTable';

function makeForm(overrides: Partial<QuestionnaireSummaryDto>): QuestionnaireSummaryDto {
  return {
    publicId: '00000000-0000-0000-0000-0000000000f1',
    title: 'Weekly check-in',
    description: null,
    type: QuestionnaireType.CheckIn,
    isActive: true,
    isDefault: false,
    questionCount: 5,
    usedForClientCount: 12,
    dateCreated: '2026-01-01T00:00:00Z',
    editedAt: '2026-01-02T00:00:00Z',
    ...overrides,
  };
}

function renderTable(form: QuestionnaireSummaryDto) {
  return renderWithProviders(
    <FormsTable
      forms={[form]}
      isPending={false}
      isError={false}
      onRetry={() => {}}
      hasActiveFilter={false}
      onClearFilters={() => {}}
    />,
  );
}

function usedForCell(): string {
  const cell = screen.getByText(/12 clients/).closest('td');
  return cell?.textContent ?? '';
}

describe('FormsTable used-for cell', () => {
  it('shows the client count and last week answer rate for a check-in form with a rate', () => {
    renderTable(makeForm({ lastWeekAnswerRatePercent: 86 }));

    expect(usedForCell()).toBe('12 clients · 86 % answered last week');
  });

  it('shows only the client count for a check-in form without a rate', () => {
    renderTable(makeForm({ lastWeekAnswerRatePercent: null }));

    expect(usedForCell()).toBe('12 clients');
  });

  it('shows only the client count for a non-check-in form', () => {
    renderTable(makeForm({ type: QuestionnaireType.Onboarding, lastWeekAnswerRatePercent: null }));

    expect(usedForCell()).toBe('12 clients');
    expect(screen.queryByText(/answered last week/)).not.toBeInTheDocument();
  });
});
