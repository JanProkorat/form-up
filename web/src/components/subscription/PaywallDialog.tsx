import { Dialog, DialogContent } from '@/components/ui/dialog';
import PaywallCard, { type PaywallFeature } from '@/components/subscription/PaywallCard';

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  feature: PaywallFeature;
}

/** The paywall as a dialog, opened when an action answers 403 SUBSCRIPTION_REQUIRED. */
export default function PaywallDialog({ open, onOpenChange, feature }: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-xl gap-0 p-7">
        <PaywallCard feature={feature} inDialog />
      </DialogContent>
    </Dialog>
  );
}
