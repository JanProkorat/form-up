import { useId, useState } from 'react';
import type { KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

interface Props {
  /** The trimmed code the coach applied; it only lives in the page's state and is sent with the checkout. Null when none. */
  appliedCode: string | null;
  /** The coach pressed Apply (or Enter) with a non-empty value; the value is already trimmed. */
  onApply: (code: string) => void;
  /** The applied code was removed, or the coach started editing it after a refusal. */
  onRemove: () => void;
  /** Translated refusal shown under the field; the typed value stays in the input. */
  error?: string | null;
  disabled?: boolean;
}

/**
 * "Have a code?" field of the checkout bar. Nothing is checked here: Apply keeps the code in the browser, and the
 * checkout request validates it. A refusal puts the input back with the typed value and `aria-invalid`.
 */
export default function PromotionCodeField({ appliedCode, onApply, onRemove, error = null, disabled = false }: Props) {
  const { t } = useTranslation();
  const inputId = useId();
  const errorId = useId();
  const [draft, setDraft] = useState(appliedCode ?? '');
  const trimmed = draft.trim();

  const apply = () => {
    if (trimmed) onApply(trimmed);
  };
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key !== 'Enter') return;
    // The field sits in the page's confirm bar: Enter applies the code and nothing else.
    event.preventDefault();
    apply();
  };

  if (appliedCode && !error) {
    return (
      <div className="flex items-center gap-2 text-body text-ink">
        <span className="font-semibold">{t('subscription.billing.promoCode.applied', { code: appliedCode })}</span>
        <Button
          type="button"
          variant="ghost"
          size="icon"
          className="size-8"
          disabled={disabled}
          aria-label={t('subscription.billing.promoCode.remove')}
          onClick={() => {
            setDraft('');
            onRemove();
          }}
        >
          <X aria-hidden="true" />
        </Button>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-1">
      <div className="flex items-center gap-2">
        <label htmlFor={inputId} className="text-body font-semibold whitespace-nowrap text-ink-2">
          {t('subscription.billing.promoCode.label')}
        </label>
        <Input
          id={inputId}
          type="text"
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          value={draft}
          disabled={disabled}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          className="h-10 min-w-0 flex-1 md:w-37.5 md:flex-none"
          onChange={(event) => {
            setDraft(event.target.value);
            // Editing after a refusal drops the refused code and its message.
            if (appliedCode) onRemove();
          }}
          onKeyDown={onKeyDown}
        />
        <Button type="button" variant="outline" className="h-10 shrink-0 px-3.5 font-semibold" disabled={disabled || !trimmed} onClick={apply}>
          {t('subscription.billing.promoCode.apply')}
        </Button>
      </div>
      {error && (
        <span id={errorId} role="alert" className="text-meta text-error">
          {error}
        </span>
      )}
    </div>
  );
}
