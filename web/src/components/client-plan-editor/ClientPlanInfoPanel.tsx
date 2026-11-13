import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Lock } from 'lucide-react';
import { PLAN_BILLING_PATH } from '@/components/subscription/planLinks';
import type { OnboardingDataDto } from '@/api/generated';
import AboutClientCard from '@/components/client-plan-editor/AboutClientCard';
import { formatCalendarDate, planEndDate, startHasArrived } from '@/components/client-plan-editor/clientPlanDates';
import StartDatePicker from '@/components/client-plan-editor/StartDatePicker';
import type { InfoPanelContext } from '@/components/plan-editor/PlanEditor';
import type { PlanTargets } from '@/components/plan-editor/plan-editor-types';
import WeekAverageCard from '@/components/plan-editor/WeekAverageCard';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { usePlanTemplate } from '@/hooks/usePlanTemplatesQueries';

const MAX_NAME_LENGTH = 200;
const MAX_KCAL = 20000;
const MAX_GRAMS = 2000;

const SECTION_LABEL_CLASS = 'text-label font-semibold tracking-label text-ink-2 uppercase';

type TargetKey = keyof PlanTargets;

const TARGET_FIELDS: { key: TargetKey; labelKey: string; unitKey: 'kcal' | 'g'; max: number }[] = [
  { key: 'kcal', labelKey: 'calories', unitKey: 'kcal', max: MAX_KCAL },
  { key: 'fiber', labelKey: 'fiber', unitKey: 'g', max: MAX_GRAMS },
  { key: 'protein', labelKey: 'protein', unitKey: 'g', max: MAX_GRAMS },
  { key: 'carbs', labelKey: 'carbs', unitKey: 'g', max: MAX_GRAMS },
  { key: 'fat', labelKey: 'fat', unitKey: 'g', max: MAX_GRAMS },
];

/** The five targets the client's onboarding holds, rounded to whole numbers like the inputs are. */
function profileTargets(onboarding: OnboardingDataDto | undefined): PlanTargets {
  const round = (value: number | null | undefined) => (value == null ? undefined : Math.round(value));
  return {
    kcal: round(onboarding?.adjustedKcal),
    protein: round(onboarding?.proteinGrams),
    carbs: round(onboarding?.carbsGrams),
    fat: round(onboarding?.fatGrams),
    fiber: round(onboarding?.fibreGrams),
  };
}

function sameTargets(left: PlanTargets, right: PlanTargets): boolean {
  return TARGET_FIELDS.every(({ key }) => left[key] === right[key]);
}

interface TargetFieldProps {
  id: string;
  label: string;
  unit: string;
  value: number | undefined;
  max: number;
  disabled: boolean;
  onChange: (value: number | undefined) => void;
}

function TargetField({ id, label, unit, value, max, disabled, onChange }: TargetFieldProps) {
  const { i18n } = useTranslation();
  const [focused, setFocused] = useState(false);
  // Grouped ("2,100") while idle; plain digits while typing so the caret stays put.
  const shown = value === undefined ? '' : focused ? String(value) : value.toLocaleString(i18n.language);
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <Label htmlFor={id}>{label}</Label>
      <div className="relative">
        <Input
          id={id}
          type="text"
          inputMode="numeric"
          disabled={disabled}
          value={shown}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
          onChange={(event) => {
            const digits = event.target.value.replace(/\D/g, '');
            if (digits === '') {
              onChange(undefined);
              return;
            }
            onChange(Math.min(Number(digits), max));
          }}
          className="h-10 pr-9"
        />
        <span className="pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 text-body text-muted-foreground">
          {unit}
        </span>
      </div>
    </div>
  );
}

interface Props extends InfoPanelContext {
  clientId: string;
  clientFirstName: string;
  /** From the client dashboard; undefined until the client has onboarding answers. */
  onboarding?: OnboardingDataDto;
  /** The start date as last saved; the server locks it once it has arrived. */
  savedStartDate?: string;
  /** Id of the template the plan was created from, if any. */
  sourceTemplateId?: string;
  /** The client is outside the coach's plan: shows a note that a higher plan unlocks editing and publishing. */
  outsidePlanNote?: boolean;
}

/** The info tab of the client plan editor: name, the client's profile, dates and daily target. */
export default function ClientPlanInfoPanel({
  doc,
  week,
  weekNumber,
  readOnly,
  onEdit,
  clientId,
  clientFirstName,
  onboarding,
  savedStartDate,
  sourceTemplateId,
  outsidePlanNote = false,
}: Props) {
  const { t, i18n } = useTranslation();
  const baseId = useId();
  const nameId = `${baseId}-name`;
  const nameInvalid = doc.name.trim() === '';
  const locale = i18n.language;

  const templateQuery = usePlanTemplate(sourceTemplateId);
  const templateName = templateQuery.data?.name;

  const profile = profileTargets(onboarding);
  const hasProfileTargets = TARGET_FIELDS.some(({ key }) => profile[key] !== undefined);
  const targetsMatchProfile = sameTargets(doc.targets, profile);

  const startLocked = startHasArrived(savedStartDate);
  const weekCount = doc.weeks.length;

  function setTarget(key: TargetKey, value: number | undefined) {
    onEdit((current) => ({ ...current, targets: { ...current.targets, [key]: value } }), `info:target:${key}`);
  }

  function renderTarget(field: (typeof TARGET_FIELDS)[number]) {
    return (
      <TargetField
        key={field.key}
        id={`${baseId}-${field.key}`}
        label={t(`planEditor.info.targets.${field.labelKey}`)}
        unit={field.unitKey === 'kcal' ? t('planEditor.info.targets.kcalUnit') : t('planEditor.units.g')}
        value={doc.targets[field.key]}
        max={field.max}
        disabled={readOnly}
        onChange={(value) => setTarget(field.key, value)}
      />
    );
  }

  return (
    <div data-testid="client-plan-info" className="flex min-h-0 flex-1 flex-col">
      <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 pt-3 pb-4.5">
        {outsidePlanNote && (
          <p data-testid="outside-plan-note" className="flex items-start gap-2 rounded-md bg-sunken p-3 text-meta text-muted-foreground">
            <Lock className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            <span>
              {t('clientPlanEditor.info.outsidePlan', { name: clientFirstName })}{' '}
              <Link
                to={PLAN_BILLING_PATH}
                className="rounded-xs font-semibold text-ink underline underline-offset-2 outline-none hover:no-underline focus-visible:ring-3 focus-visible:ring-ring/50"
              >
                {t('subscription.outsidePlan.upgrade')}
              </Link>
            </span>
          </p>
        )}
        <div className="flex flex-col gap-1">
          <Label htmlFor={nameId}>
            {t('clientPlanEditor.info.name')} <span className="text-destructive">*</span>
          </Label>
          <Input
            id={nameId}
            value={doc.name}
            maxLength={MAX_NAME_LENGTH}
            disabled={readOnly}
            aria-invalid={nameInvalid}
            aria-describedby={nameInvalid ? `${nameId}-error` : undefined}
            onChange={(event) => onEdit((current) => ({ ...current, name: event.target.value }), 'name')}
            className="h-10"
          />
          {nameInvalid && (
            <p id={`${nameId}-error`} data-testid="info-name-error" role="alert" className="text-meta text-destructive">
              {t('planEditor.info.nameRequired')}
            </p>
          )}
        </div>

        <AboutClientCard clientId={clientId} clientFirstName={clientFirstName} onboarding={onboarding} />

        <div className="flex flex-col gap-2">
          <span className={SECTION_LABEL_CLASS}>{t('clientPlanEditor.info.dates')}</span>
          <div className="grid grid-cols-[3fr_2fr] gap-x-3">
            <div className="flex min-w-0 flex-col gap-1">
              <Label htmlFor={`${baseId}-start`}>{t('clientPlanEditor.info.starts')}</Label>
              <StartDatePicker
                id={`${baseId}-start`}
                value={doc.startDate}
                disabled={readOnly || startLocked}
                disabledTitle={startLocked ? t('apiErrors.START_DATE_LOCKED') : undefined}
                onChange={(value) => onEdit((current) => ({ ...current, startDate: value }), 'startDate')}
              />
            </div>
            <div className="flex min-w-0 flex-col gap-1">
              <span className="text-body font-medium text-ink">{t('clientPlanEditor.info.ends')}</span>
              <div
                data-testid="plan-end-date"
                className="flex h-10 items-center truncate rounded-field border border-input bg-background px-2.5 text-body font-semibold text-foreground"
              >
                {doc.startDate && weekCount > 0 ? formatCalendarDate(planEndDate(doc.startDate, weekCount), locale) : '—'}
              </div>
            </div>
          </div>
          <p className="text-meta text-muted-foreground">{t('clientPlanEditor.info.datesHint', { count: weekCount })}</p>
          {startLocked && (
            <p data-testid="plan-start-locked" className="text-meta text-muted-foreground">
              {t('apiErrors.START_DATE_LOCKED')}
            </p>
          )}
        </div>

        <div className="flex flex-col gap-2">
          <span className={SECTION_LABEL_CLASS}>{t('planEditor.info.targets.title')}</span>
          <div className="grid grid-cols-2 gap-x-3 gap-y-3">{TARGET_FIELDS.slice(0, 2).map(renderTarget)}</div>
          <div className="grid grid-cols-3 gap-x-3">{TARGET_FIELDS.slice(2).map(renderTarget)}</div>
          {hasProfileTargets && (
            <p data-testid="targets-copied" className="text-meta text-muted-foreground">
              {t('clientPlanEditor.info.copiedFrom', { name: clientFirstName })}
              {' · '}
              <button
                type="button"
                data-testid="targets-reset"
                disabled={readOnly || targetsMatchProfile}
                onClick={() => onEdit((current) => ({ ...current, targets: { ...profile } }), 'info:targets-reset')}
                className="cursor-pointer rounded-xs font-semibold text-ink underline underline-offset-2 outline-none hover:no-underline focus-visible:ring-3 focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:no-underline disabled:opacity-60"
              >
                {t('clientPlanEditor.info.resetToProfile')}
              </button>
            </p>
          )}
        </div>

        <WeekAverageCard week={week} weekNumber={weekNumber} targets={doc.targets} />
      </div>

      {templateName && (
        <div
          data-testid="plan-source-template"
          className="shrink-0 border-t border-line px-4 py-2.5 text-body text-muted-foreground"
        >
          {t('clientPlanEditor.info.startedFromTemplate', { name: templateName })}
        </div>
      )}
    </div>
  );
}
