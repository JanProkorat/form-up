import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ClientFormAnswer } from '@/api/questionnaires';
import ClientFormAnswers from '@/components/client-forms/ClientFormAnswers';
import { renderWithProviders } from '@/test/render';

function renderAnswer(answer: ClientFormAnswer) {
  return renderWithProviders(
    <ClientFormAnswers answers={[{ questionPublicId: 'q1', questionLabel: 'Question', ...answer }]} />,
  );
}

describe('ClientFormAnswers', () => {
  it('marks the "Other" text of a multi-select apart from the chosen options', () => {
    renderAnswer({
      questionType: 'multi_select',
      valueJson: JSON.stringify(['Eggs', 'Nuts']),
      valueText: 'Shellfish',
    });

    const chips = screen.getAllByRole('listitem').filter((li) => li.className.includes('rounded-full'));
    expect(chips.map((chip) => chip.textContent)).toEqual(['Eggs', 'Nuts', 'Other: Shellfish']);
  });

  it('shows only the chosen options when a multi-select has no "Other" text', () => {
    renderAnswer({ questionType: 'multi_select', valueJson: JSON.stringify(['Eggs']) });

    expect(screen.getByText('Eggs')).toBeInTheDocument();
    expect(screen.queryByText(/^Other:/)).not.toBeInTheDocument();
  });

  it('shows a star rating out of 5 with the star icon', () => {
    const { container } = renderAnswer({ questionType: 'star_rating', valueNumber: 4 });

    expect(screen.getByText('4 / 5')).toBeInTheDocument();
    expect(container.querySelector('svg.lucide-star')).toHaveAttribute('aria-hidden', 'true');
  });

  it('shows the chosen face of a smiley rating out of 5', () => {
    const { container } = renderAnswer({ questionType: 'smiley_rating', valueNumber: 2 });

    expect(screen.getByText('2 / 5')).toBeInTheDocument();
    expect(container.querySelector('svg.lucide-frown')).toHaveAttribute('aria-hidden', 'true');
  });

  it('falls back to the generic smile for an out-of-range smiley value', () => {
    const { container } = renderAnswer({ questionType: 'smiley_rating', valueNumber: 7 });

    expect(screen.getByText('7 / 5')).toBeInTheDocument();
    expect(container.querySelector('svg.lucide-smile')).toBeInTheDocument();
  });

  it('shows a scale against its configured max, without a star', () => {
    const { container } = renderAnswer({ questionType: 'scale', valueNumber: 3, scaleMax: 5 });

    expect(screen.getByText('3 / 5')).toBeInTheDocument();
    expect(container.querySelector('svg.lucide-star')).not.toBeInTheDocument();
  });

  it('falls back to a max of 10 when a scale has no configured max', () => {
    renderAnswer({ questionType: 'scale', valueNumber: 3 });

    expect(screen.getByText('3 / 10')).toBeInTheDocument();
  });

  it('shows the no-answer state when a scale has no value', () => {
    renderAnswer({ questionType: 'scale', scaleMax: 5 });

    const row = screen.getByText('Question').closest('li');
    expect(row).not.toBeNull();
    expect(within(row as HTMLElement).getByText('No answer')).toBeInTheDocument();
  });

  it('links an uploaded file with a translated label instead of the signed URL', () => {
    const url = 'https://files.example.test/questionnaire-files/r1/u1.png?X-Amz-Signature=abc';
    renderAnswer({ questionType: 'file_upload', fileUrl: url });

    const link = screen.getByRole('link', { name: 'Open file' });
    expect(link).toHaveAttribute('href', url);
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(screen.queryByText(/X-Amz-Signature/)).not.toBeInTheDocument();
  });

  it.each([
    ['an empty string', ''],
    ['no', undefined],
  ])('shows the no-answer state for a file with %s URL', (_label, fileUrl) => {
    renderAnswer({ questionType: 'file_upload', fileUrl });

    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.getByText('No answer')).toBeInTheDocument();
  });

  it('still shows a short text answer as plain text', () => {
    renderAnswer({ questionType: 'short_text', valueText: 'Hello there' });

    expect(screen.getByText('Hello there')).toBeInTheDocument();
  });
});
