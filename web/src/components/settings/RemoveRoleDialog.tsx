import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { CirclePlay, Dumbbell, Info, Leaf, Lock, Users } from 'lucide-react';
import { cn } from '@/lib/utils';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';

export type CoachRoleKey = 'trainer' | 'nutritionist';

interface Props {
  role: CoachRoleKey | null;
  /** Counts for the role, read from GET /users/me/roles before the removal. */
  clientCount: number;
  sharedWithOtherRoleCount: number;
  pending: boolean;
  errorMessage: string | null;
  /** When the second-role add-on is billed: when it stops and the new monthly payment; absent otherwise. */
  billingNote?: string | null;
  onConfirm: () => void;
  onClose: () => void;
}

const ROLE_VISUALS = {
  trainer: { Icon: Dumbbell, tint: 'bg-sunken text-ink' },
  nutritionist: { Icon: Leaf, tint: 'bg-nutrition-soft text-nutrition' },
} as const;

function Row({ icon, title, children }: { icon: ReactNode; title: string; children?: ReactNode }) {
  return (
    <li className="flex gap-3">
      <span className="flex size-8 shrink-0 items-center justify-center rounded-tile bg-sunken text-ink-2">{icon}</span>
      <div className="flex flex-col gap-0.5">
        <span className="text-copy font-semibold text-ink">{title}</span>
        {children && <span className="text-body leading-snug text-muted-foreground">{children}</span>}
      </div>
    </li>
  );
}

/** Confirm dialog for removing one coach role; N, M and K = N - M come from the pre-removal counts. */
export default function RemoveRoleDialog({
  role,
  clientCount,
  sharedWithOtherRoleCount,
  pending,
  errorMessage,
  billingNote,
  onConfirm,
  onClose,
}: Props) {
  const { t } = useTranslation();
  const visuals = role ? ROLE_VISUALS[role] : null;
  const prefix = role ? `settings.roles.removeDialog.${role}` : '';

  // The shared count can never exceed the total; clamp so a stale pair cannot produce a negative K.
  const shared = Math.min(sharedWithOtherRoleCount, clientCount);
  const rest = clientCount - shared;

  return (
    <Dialog open={role !== null} onOpenChange={(open) => !open && !pending && onClose()}>
      <DialogContent className="max-w-125 gap-5 rounded-xl px-7 pt-6.5 pb-6 sm:max-w-125">
        {role && visuals && (
          <>
            <DialogHeader className="flex-row items-start gap-3.5 text-left">
              <span className={cn('flex size-11 shrink-0 items-center justify-center rounded-xl', visuals.tint)}>
                <visuals.Icon className="size-4.5" aria-hidden="true" />
              </span>
              <div className="flex flex-col gap-1.25">
                <DialogTitle className="text-auth-title leading-tight">{t(`${prefix}.title`)}</DialogTitle>
                <DialogDescription className="text-copy leading-normal">{t(`${prefix}.description`)}</DialogDescription>
              </div>
            </DialogHeader>

            <ul className="flex flex-col gap-4">
              <Row
                icon={<Users className="size-4" aria-hidden="true" />}
                title={t('settings.roles.removeDialog.notified', { count: clientCount })}
              >
                {clientCount > 0 &&
                  [
                    shared > 0 ? t(`${prefix}.shared`, { count: shared }) : null,
                    rest > 0
                      ? shared > 0
                        ? t(`${prefix}.rest`, { count: rest })
                        : t(`${prefix}.restAll`, { count: rest })
                      : null,
                  ]
                    .filter(Boolean)
                    .join(' ')}
              </Row>
              <Row icon={<CirclePlay className="size-4" aria-hidden="true" />} title={t(`${prefix}.plansTitle`)}>
                {t(`${prefix}.plansBody`)}
              </Row>
              <Row icon={<Lock className="size-4" aria-hidden="true" />} title={t(`${prefix}.libraryTitle`)}>
                {t(`${prefix}.libraryBody`)}
              </Row>
            </ul>

            <div className="flex gap-2.5 rounded-xl bg-sunken px-3.5 py-3 text-ink-2">
              <Info className="mt-px size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
              <p className="text-body leading-normal">{t('settings.roles.removeDialog.footnote')}</p>
            </div>

            {billingNote && (
              <div className="flex gap-2.5 rounded-xl bg-sunken px-3.5 py-3 text-ink-2">
                <Info className="mt-px size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <p className="text-body leading-normal">{billingNote}</p>
              </div>
            )}

            {errorMessage && (
              <p role="alert" className="text-body text-error">
                {errorMessage}
              </p>
            )}

            <DialogFooter>
              <Button type="button" variant="outline" size="lg" disabled={pending} onClick={onClose}>
                {t('settings.roles.removeDialog.keep')}
              </Button>
              <Button
                type="button"
                size="lg"
                disabled={pending}
                onClick={onConfirm}
                className="bg-error text-primary-foreground hover:bg-error/90"
              >
                {t(`${prefix}.confirm`)}
              </Button>
            </DialogFooter>
          </>
        )}
      </DialogContent>
    </Dialog>
  );
}
