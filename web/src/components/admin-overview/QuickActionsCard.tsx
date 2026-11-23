import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Plus, Search } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

const COACHES_PATH = '/admin/coach-accounts';
const NEW_PROMOTION_PATH = '/admin/promotion-offers?new=1';

/** Find a coach (opens the Coaches list searched for the text) and start a new promotion. */
export default function QuickActionsCard() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [text, setText] = useState('');

  const handleSearch = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const term = text.trim();
    navigate(term ? `${COACHES_PATH}?${new URLSearchParams({ q: term }).toString()}` : COACHES_PATH);
  };

  return (
    <section
      aria-labelledby="overview-quick-actions"
      className="flex flex-col gap-3.5 rounded-2xl border border-line bg-card p-4.5 shadow-panel"
    >
      <h2 id="overview-quick-actions" className="font-display text-card-title font-semibold text-ink">
        {t('adminOverview.quick.title')}
      </h2>

      <form onSubmit={handleSearch} role="search" className="flex flex-col gap-1.75">
        <label htmlFor="overview-coach-search" className="text-body font-semibold text-ink-2">
          {t('adminOverview.quick.findCoach')}
        </label>
        <div className="relative">
          <Search
            className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          />
          <Input
            id="overview-coach-search"
            type="search"
            value={text}
            onChange={(event) => setText(event.target.value)}
            placeholder={t('adminOverview.quick.searchPlaceholder')}
            className="h-10 pl-9"
          />
        </div>
      </form>

      <Button type="button" size="lg" className="gap-1.75 font-semibold" onClick={() => navigate(NEW_PROMOTION_PATH)}>
        <Plus aria-hidden="true" />
        {t('adminOverview.quick.newPromotion')}
      </Button>
    </section>
  );
}
