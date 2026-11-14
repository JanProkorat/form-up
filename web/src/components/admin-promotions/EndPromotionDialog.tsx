import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CircleMinus, Info } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog';
import { DialogIconTile } from '@/components/admin-coaches/ReasonDialog';

interface Props {
  /** The promotion to end; the dialog is closed when this is null. */
  promotion: { name: string; usedCount: number } | null;
  onOpenChange: (open: boolean) => void;
  /** Ends the promotion; resolves true when the dialog should close. */
  run: () => Promise<boolean>;
}

/** Confirms ending a promotion now. Nothing runs until the admin confirms. */
export default function EndPromotionDialog({ promotion, onOpenChange, run }: Props) {
  const { t } = useTranslation();
  const [isRunning, setIsRunning] = useState(false);

  const confirm = async () => {
    setIsRunning(true);
    try {
      if (await run()) {
        onOpenChange(false);
      }
    } finally {
      setIsRunning(false);
    }
  };

  return (
    <Dialog open={promotion !== null} onOpenChange={(next) => (isRunning ? undefined : onOpenChange(next))}>
      <DialogContent className="gap-0 overflow-hidden p-0 sm:max-w-110">
        {promotion && (
          <div className="flex flex-col">
            <div className="flex items-center gap-3.5 px-6 pt-5.5 pr-14">
              <DialogIconTile tone="red">
                <CircleMinus />
              </DialogIconTile>
              <DialogTitle className="font-display text-panel-title leading-snug">
                {t('adminPromotions.end.title', { name: promotion.name })}
              </DialogTitle>
            </div>
            <div className="flex flex-col gap-4 px-6 pt-2.5 pb-5.5">
              <DialogDescription className="text-copy text-muted-foreground">
                {t('adminPromotions.end.description')}
              </DialogDescription>
              {promotion.usedCount > 0 && (
                <div className="flex gap-2.5 rounded-xl bg-sunken px-3.5 py-3 text-ink-2">
                  <Info aria-hidden="true" className="mt-px size-4 shrink-0" />
                  <p className="text-body leading-normal">{t('adminPromotions.end.note', { count: promotion.usedCount })}</p>
                </div>
              )}
            </div>
            <div className="flex justify-end gap-2 border-t border-line px-6 py-4">
              <Button type="button" variant="outline" size="lg" disabled={isRunning} onClick={() => onOpenChange(false)}>
                {t('common.cancel')}
              </Button>
              <Button
                type="button"
                size="lg"
                variant="destructive"
                className="bg-error text-primary-foreground hover:bg-error/90"
                disabled={isRunning}
                onClick={() => void confirm()}
              >
                {t('adminPromotions.end.confirm')}
              </Button>
            </div>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
