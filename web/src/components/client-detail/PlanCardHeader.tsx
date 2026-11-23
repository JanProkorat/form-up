import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { ChevronRight } from 'lucide-react';
import { cn } from '@/lib/utils';

interface Props {
  tone: 'nutrition' | 'training';
  label: string;
  heading: string;
  /** Id for the heading, so the card's `<section>` can reference it via aria-labelledby. */
  headingId: string;
  /** Destination of the "Open plan" link; without it no link renders. */
  openPlanTo?: string;
}

/**
 * Header row of a plan card: coloured dot + kicker label + muted heading on
 * the left, and an optional "Open plan" link on the right. Only the nutrition
 * card passes a destination (the client's Nutrition tab); the web has no
 * training plan view for a client, so the training card shows no link.
 */
export default function PlanCardHeader({ tone, label, heading, headingId, openPlanTo }: Props) {
  const { t } = useTranslation();
  const isNutrition = tone === 'nutrition';

  return (
    <div className="flex items-center justify-between gap-3">
      <div className="flex min-w-0 items-center gap-2">
        <span
          aria-hidden="true"
          className={cn('size-2 shrink-0 rounded-full', isNutrition ? 'bg-nutrition' : 'bg-training')}
        />
        <span
          className={cn(
            'shrink-0 text-caption font-semibold uppercase tracking-wider',
            isNutrition ? 'text-nutrition-ink' : 'text-training-ink',
          )}
        >
          {label}
        </span>
        <h2 id={headingId} className="truncate text-caption text-muted-foreground">{heading}</h2>
      </div>
      {openPlanTo && (
        <Link
          to={openPlanTo}
          className="flex shrink-0 items-center gap-0.5 text-body font-medium text-ink hover:underline"
        >
          {t('clientDetail.overview.mealPlan.openPlan')}
          <ChevronRight className="size-4" aria-hidden="true" />
        </Link>
      )}
    </div>
  );
}
