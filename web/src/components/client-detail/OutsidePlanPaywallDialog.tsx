import OutsidePlanPaywall from '@/components/client-detail/OutsidePlanPaywall';
import { Dialog, DialogContent } from '@/components/ui/dialog';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  firstName: string;
}

/** The outside-plan paywall as a dialog, opened when the coach tries to change or publish an outside-plan client's plan. */
export default function OutsidePlanPaywallDialog({ open, onOpenChange, firstName }: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-xl gap-0 p-7">
        <OutsidePlanPaywall firstName={firstName} inDialog />
      </DialogContent>
    </Dialog>
  );
}
