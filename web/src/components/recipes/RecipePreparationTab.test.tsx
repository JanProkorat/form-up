import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import RecipePreparationTab from '@/components/recipes/RecipePreparationTab';
import type { StepItem } from '@/components/recipes/recipe-form-types';
import { renderWithProviders } from '@/test/render';

const BORDER = 2;
const LINE = 20;

// jsdom has no layout: one line per 10 characters, and a 1px border on each side that
// scrollHeight/clientHeight exclude but offsetHeight includes.
function contentHeight(element: HTMLTextAreaElement): number {
  return Math.max(1, Math.ceil(element.value.length / 10)) * LINE;
}

const originals = {
  scrollHeight: Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'scrollHeight'),
  clientHeight: Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'clientHeight'),
  offsetHeight: Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'offsetHeight'),
};

function restore(name: keyof typeof originals) {
  const original = originals[name];
  if (original) {
    Object.defineProperty(HTMLTextAreaElement.prototype, name, original);
  } else {
    delete (HTMLTextAreaElement.prototype as unknown as Record<string, unknown>)[name];
  }
}

function Harness({ initial }: { initial: string }) {
  const [steps, setSteps] = useState<StepItem[]>([{ id: 'step-1', text: initial, title: '', minutesText: '' }]);
  return (
    <RecipePreparationTab steps={steps} onStepsChange={setSteps} readOnly={false} timesSummary={null} showErrors={false} />
  );
}

describe('RecipePreparationTab step description box', () => {
  beforeEach(() => {
    Object.defineProperty(HTMLTextAreaElement.prototype, 'scrollHeight', {
      configurable: true,
      get(this: HTMLTextAreaElement) {
        return contentHeight(this);
      },
    });
    Object.defineProperty(HTMLTextAreaElement.prototype, 'clientHeight', {
      configurable: true,
      get(this: HTMLTextAreaElement) {
        return contentHeight(this);
      },
    });
    Object.defineProperty(HTMLTextAreaElement.prototype, 'offsetHeight', {
      configurable: true,
      get(this: HTMLTextAreaElement) {
        return contentHeight(this) + BORDER;
      },
    });
  });

  afterEach(() => {
    restore('scrollHeight');
    restore('clientHeight');
    restore('offsetHeight');
  });

  it('includes the border in the height on first render with saved text', () => {
    renderWithProviders(<Harness initial={'x'.repeat(25)} />);

    expect(screen.getByLabelText('Step 1', { exact: true })).toHaveStyle({ height: `${3 * LINE + BORDER}px` });
  });

  it('grows and shrinks with the typed text', async () => {
    const { user } = renderWithProviders(<Harness initial="short" />);
    const box = screen.getByLabelText('Step 1', { exact: true });
    expect(box).toHaveStyle({ height: `${LINE + BORDER}px` });

    await user.type(box, 'y'.repeat(20));
    expect(box).toHaveStyle({ height: `${3 * LINE + BORDER}px` });

    await user.clear(box);
    expect(box).toHaveStyle({ height: `${LINE + BORDER}px` });
  });
});
