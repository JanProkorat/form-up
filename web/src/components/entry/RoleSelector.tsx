import { DumbbellIcon, LeafIcon, UserIcon } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '@/lib/utils';

import { toggleRole, type RegistrableRole } from '@/components/register/roles';

interface RoleCardStyle {
  icon: LucideIcon;
  iconBox: string;
  selected: string;
  indicator: string;
}

const ROLE_CARDS: { role: RegistrableRole; style: RoleCardStyle }[] = [
  {
    role: 'Trainer',
    style: {
      icon: DumbbellIcon,
      iconBox: 'bg-training-soft text-training',
      selected: 'border-training bg-training-soft ring-1 ring-inset ring-training',
      indicator: 'border-5 border-training bg-surface',
    },
  },
  {
    role: 'Nutritionist',
    style: {
      icon: LeafIcon,
      iconBox: 'bg-nutrition-soft text-nutrition',
      selected: 'border-nutrition bg-nutrition-soft ring-1 ring-inset ring-nutrition',
      indicator: 'border-5 border-nutrition bg-surface',
    },
  },
  {
    role: 'Client',
    style: {
      icon: UserIcon,
      iconBox: 'bg-error-soft text-marker',
      selected: 'border-marker bg-error-soft ring-1 ring-inset ring-marker',
      indicator: 'border-5 border-marker bg-surface',
    },
  },
];

interface RoleSelectorProps {
  value: RegistrableRole[];
  onChange: (roles: RegistrableRole[]) => void;
  /**
   * Forwards RHF Controller's `field.onBlur` so a real blur on one of these
   * buttons marks the field "touched" (RegisterForm uses `mode: 'onTouched'`)
   * and it revalidates on every later change, not only on blur. Without it a
   * role toggle never told react-hook-form anything, so `formState.isValid`
   * stayed stale and the submit button never unlocked.
   */
  onBlur?: () => void;
  error?: string;
  /** Fixes the current selection: the cards stay visible but can no longer be toggled. */
  locked?: boolean;
}

/**
 * Register-page role picker: Personal trainer, Nutritionist and "I train for
 * myself" (Client) cards. The coach roles combine; Client excludes them
 * (see `toggleRole`).
 */
export default function RoleSelector({
  value,
  onChange,
  onBlur,
  error,
  locked = false,
}: RoleSelectorProps) {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col gap-2 short:gap-1.5">
      <span className="text-body font-medium text-ink-2">{t('entry.register.roleLabel')}</span>
      <div
        role="group"
        aria-label={t('entry.register.roleLabel')}
        aria-describedby={error ? 'entry-role-error' : undefined}
        className="grid grid-cols-1 gap-2.5 sm:grid-cols-3"
      >
        {ROLE_CARDS.map(({ role, style }) => {
          const selected = value.includes(role);
          const Icon = style.icon;
          const key = role.toLowerCase();
          return (
            <button
              key={role}
              type="button"
              aria-pressed={selected}
              disabled={locked}
              onClick={() => onChange(toggleRole(value, role))}
              onBlur={onBlur}
              className={cn(
                'flex flex-col gap-2 rounded-2xl border border-border bg-surface p-3.5 text-left outline-none focus-visible:ring-3 focus-visible:ring-ring/50 short:flex-row short:items-center short:gap-2 short:p-2.5',
                selected && style.selected,
                locked && !selected && 'opacity-50'
              )}
            >
              {/* On short screens the wrapper dissolves so icon, name and radio sit in one row. */}
              <span className="flex items-center short:contents">
                <span
                  className={cn(
                    'flex size-8.5 shrink-0 items-center justify-center rounded-field short:size-7',
                    style.iconBox
                  )}
                >
                  <Icon className="size-4.5 short:size-4" aria-hidden="true" />
                </span>
                <span
                  aria-hidden="true"
                  className={cn(
                    'ml-auto box-border size-4.5 shrink-0 rounded-full border-[1.5px] border-muted-foreground short:order-last',
                    selected && style.indicator
                  )}
                />
              </span>
              <span className="text-subhead font-bold text-ink short:text-body short:leading-tight">
                {t(`entry.register.roles.${key}.name`)}
              </span>
              <span className="text-meta leading-normal text-muted-foreground short:hidden">
                {t(`entry.register.roles.${key}.description`)}
              </span>
            </button>
          );
        })}
      </div>
      <p className="text-caption text-faint">{t('entry.register.roleHint')}</p>
      {error && (
        <p id="entry-role-error" className="text-meta text-destructive">
          {error}
        </p>
      )}
    </div>
  );
}
