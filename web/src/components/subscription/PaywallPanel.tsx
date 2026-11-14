import PaywallCard, { type PaywallFeature } from '@/components/subscription/PaywallCard';

/** The paywall card centred in the space a locked page or tab would have filled. */
export default function PaywallPanel({ feature }: { feature: PaywallFeature }) {
  return (
    <div className="flex flex-1 items-start justify-center pt-10">
      <PaywallCard feature={feature} />
    </div>
  );
}
