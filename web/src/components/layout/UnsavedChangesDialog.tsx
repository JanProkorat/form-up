import { useTranslation } from 'react-i18next';
import type { Blocker } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';

interface Props {
  /** The blocker from `useUnsavedChangesBlocker`. */
  blocker: Blocker;
  /** Copy lives at `<i18nPrefix>.{title,body,stay,discard}`. */
  i18nPrefix: 'forms.builder.leave' | 'profile.page.leave';
}

/** Asks whether to leave a page with unsaved edits; shown while the router holds the navigation. */
export default function UnsavedChangesDialog({ blocker, i18nPrefix }: Props) {
  const { t } = useTranslation();
  return (
    <Dialog
      open={blocker.state === 'blocked'}
      onOpenChange={(open) => {
        if (!open && blocker.state === 'blocked') {
          blocker.reset();
        }
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{t(`${i18nPrefix}.title`)}</DialogTitle>
          <DialogDescription>{t(`${i18nPrefix}.body`)}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => blocker.state === 'blocked' && blocker.reset()}>
            {t(`${i18nPrefix}.stay`)}
          </Button>
          <Button type="button" variant="destructive" onClick={() => blocker.state === 'blocked' && blocker.proceed()}>
            {t(`${i18nPrefix}.discard`)}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
