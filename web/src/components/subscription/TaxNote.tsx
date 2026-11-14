import { useTranslation } from 'react-i18next';
import type { BillingTaxMode } from '@/api/generated';
import { vatRatePercent } from '@/components/subscription/planPricing';

interface Props {
  taxMode: BillingTaxMode | undefined;
  className?: string;
}

/** "Prices without VAT, 21 % VAT is added": rendered only when the server says VAT applies. */
export default function TaxNote({ taxMode, className }: Props) {
  const { t } = useTranslation();
  const rate = vatRatePercent(taxMode);
  if (rate === null) return null;
  return <p className={className ?? 'text-body text-muted-foreground'}>{t('subscription.billing.vat', { rate })}</p>;
}
