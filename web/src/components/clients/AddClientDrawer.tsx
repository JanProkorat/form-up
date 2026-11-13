import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useTranslation } from 'react-i18next';
import { Check, Send } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import DiscountPicker from '@/components/offer/DiscountPicker';
import OfferMessage from '@/components/offer/OfferMessage';
import OfferPayable from '@/components/offer/OfferPayable';
import OnboardingFormRow, { NO_FORM_ID, type OfferForm } from '@/components/offer/OnboardingFormRow';
import PackageTiles, { NO_PACKAGE_ID } from '@/components/offer/PackageTiles';
import {
  OFFER_MESSAGE_MAX_LENGTH,
  discountBlock,
  offerPrice,
  toOfferPackage,
  usableDiscounts,
  type OfferPackage,
} from '@/components/offer/offerModel';
import { LinkCapabilityScope, QuestionnaireType } from '@/api/generated';
import { useCreatePendingInvite, usePendingInviteSlotCheck } from '@/hooks/useClientsQueries';
import { useDebouncedValue } from '@/hooks/useDebouncedValue';
import { useTrainerQuestionnaires } from '@/hooks/useQuestionnaireQueries';
import { isNotEntitled } from '@/components/client-detail/payment/paymentErrors';
import { usePaymentDiscounts, usePaymentPackages } from '@/hooks/useClientPaymentQueries';
import ClientLimitDialog from '@/components/subscription/ClientLimitDialog';
import { isClientLimitReached } from '@/lib/subscriptionErrors';
import { useAuthStore } from '@/stores/auth';
import { cn } from '@/lib/utils';

const EMAIL_MAX_LENGTH = 100;
const SLOT_CHECK_DEBOUNCE_MS = 400;

interface FormValues {
  email: string;
}

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

const SCOPE_OPTIONS: { value: LinkCapabilityScope; labelKey: string }[] = [
  { value: LinkCapabilityScope.TrainingOnly, labelKey: 'clients.addClientDrawer.scope.training' },
  { value: LinkCapabilityScope.NutritionOnly, labelKey: 'clients.addClientDrawer.scope.nutrition' },
  { value: LinkCapabilityScope.Both, labelKey: 'clients.addClientDrawer.scope.both' },
];

/**
 * The "Invite client" drawer. `requestedScope` is sent only by a professional
 * holding both roles; a single-role professional sends none, so the server
 * defaults to the roles they hold.
 */
export default function AddClientDrawer({ open, onOpenChange }: Props) {
  const { t } = useTranslation();

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="w-full gap-0 p-0 sm:w-drawer sm:max-w-none">
        <SheetHeader className="gap-1 border-b border-border px-6 py-5">
          <SheetTitle className="font-display text-card-title">{t('clients.addClientDrawer.title')}</SheetTitle>
          <SheetDescription className="text-body">{t('clients.addClientDrawer.description')}</SheetDescription>
        </SheetHeader>
        <InviteForm onClose={() => onOpenChange(false)} />
      </SheetContent>
    </Sheet>
  );
}

function SectionTitle({ children }: { children: string }) {
  return <h3 className="text-label font-bold tracking-label text-muted-foreground uppercase">{children}</h3>;
}

function InviteForm({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const roles = useAuthStore((state) => state.user?.roles ?? []);
  const isDualRole = roles.includes('Trainer') && roles.includes('Nutritionist');

  const [scope, setScope] = useState<LinkCapabilityScope>(LinkCapabilityScope.Both);
  // Untouched choices follow the defaults: no package, and the coach's default form once the list has loaded.
  const [packageChoice, setPackageChoice] = useState(NO_PACKAGE_ID);
  const [discountChoice, setDiscountChoice] = useState('');
  const [formChoice, setFormChoice] = useState<string | null>(null);
  const [message, setMessage] = useState('');
  const [capOpen, setCapOpen] = useState(false);

  const schema = z.object({
    email: z
      .string()
      .min(1, t('clients.addClientDrawer.validation.emailRequired'))
      .email(t('clients.addClientDrawer.validation.emailInvalid'))
      .max(EMAIL_MAX_LENGTH, t('clients.addClientDrawer.validation.emailTooLong')),
  });

  const {
    register,
    handleSubmit,
    control,
    formState: { errors, isValid },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onTouched',
    defaultValues: { email: '' },
  });

  // Live "already has a coach" hint: only for an address that passes the schema, after the typing settles.
  const typedEmail = useWatch({ control, name: 'email' }).trim();
  const debouncedEmail = useDebouncedValue(typedEmail, SLOT_CHECK_DEBOUNCE_MS);
  const slotCheck = usePendingInviteSlotCheck(debouncedEmail, schema.shape.email.safeParse(debouncedEmail).success);
  const slotLabels: string[] = [];
  if (slotCheck.data && debouncedEmail === typedEmail) {
    if (slotCheck.data.trainingTaken && scope !== LinkCapabilityScope.NutritionOnly) {
      slotLabels.push(t('clients.addClientDrawer.slotTaken.training', { email: typedEmail }));
    }
    if (slotCheck.data.nutritionTaken && scope !== LinkCapabilityScope.TrainingOnly) {
      slotLabels.push(t('clients.addClientDrawer.slotTaken.nutrition', { email: typedEmail }));
    }
  }

  const formsQuery = useTrainerQuestionnaires({ type: QuestionnaireType.Onboarding });
  const forms: OfferForm[] = (formsQuery.data ?? [])
    .filter((form) => form.isActive)
    .map((form) => ({
      publicId: form.publicId,
      title: form.title,
      questionCount: form.questionCount,
      isDefault: form.isDefault,
    }));
  const defaultFormId = forms.find((form) => form.isDefault)?.publicId ?? NO_FORM_ID;
  const formId = formChoice ?? defaultFormId;

  const packagesQuery = usePaymentPackages(true);
  const discountsQuery = usePaymentDiscounts(packagesQuery.isSuccess);
  // Not entitled (or a plan that lost payments while the drawer was open): the offer's payment pieces disappear.
  const notEntitled =
    (packagesQuery.isError && isNotEntitled(packagesQuery.error)) ||
    (discountsQuery.isError && isNotEntitled(discountsQuery.error));
  const packages: OfferPackage[] = (packagesQuery.data ?? []).map(toOfferPackage);
  const discounts = discountsQuery.data ?? [];
  // A package or discount archived since the lists loaded drops out of the lists and so out of the request.
  const offeredPackage = notEntitled ? undefined : packages.find((pkg) => pkg.id === packageChoice);
  const offeredDiscount = usableDiscounts(discounts).find(
    (discount) => discount.discountId === discountChoice && discountBlock(discount, offeredPackage) === null,
  );

  function onPackageSelect(nextPackageId: string) {
    setPackageChoice(nextPackageId);
    // A discount that does not cover the new package would be rejected by the server.
    const nextPackage = packages.find((pkg) => pkg.id === nextPackageId);
    if (!offeredDiscount || discountBlock(offeredDiscount, nextPackage) !== null) {
      setDiscountChoice('');
    }
  }

  const inviteMutation = useCreatePendingInvite();
  const messageTooLong = message.length > OFFER_MESSAGE_MAX_LENGTH;
  const listsLoading = !notEntitled && (packagesQuery.isPending || (packagesQuery.isSuccess && discountsQuery.isPending));

  function onSubmit(values: FormValues) {
    // Skip is sent only when the coach explicitly picked "None"; an untouched row never skips.
    const skipOnboardingForm = formChoice === NO_FORM_ID;
    const questionnairePublicId = formChoice ?? (defaultFormId === NO_FORM_ID ? null : defaultFormId);
    inviteMutation.mutate(
      {
        email: values.email,
        message: message.trim() || null,
        ...(skipOnboardingForm
          ? { skipOnboardingForm: true, questionnairePublicId: null }
          : questionnairePublicId
            ? { questionnairePublicId }
            : {}),
        ...(isDualRole ? { requestedScope: scope } : {}),
        ...(offeredPackage
          ? { packageId: offeredPackage.id, ...(offeredDiscount ? { discountId: offeredDiscount.discountId } : {}) }
          : {}),
      },
      { onSuccess: onClose, onError: (error) => isClientLimitReached(error) && setCapOpen(true) },
    );
  }

  return (
    <>
    <form onSubmit={handleSubmit(onSubmit)} className="flex min-h-0 flex-1 flex-col">
      <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-5">
        <section className="flex flex-col gap-3">
          <SectionTitle>{t('clients.addClientDrawer.sections.client')}</SectionTitle>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="add-client-email">
              {t('clients.addClientDrawer.emailLabel')}
              <span className="text-error" aria-hidden="true">
                {' *'}
              </span>
            </Label>
            <Input
              id="add-client-email"
              type="email"
              autoComplete="email"
              className="h-10"
              aria-invalid={!!errors.email}
              {...register('email')}
            />
            {/* Always mounted so a screen reader announces content swaps (error, slot label, helper). */}
            <div aria-live="polite" className="flex flex-col gap-1">
              {errors.email ? (
                <p className="text-meta text-destructive">{errors.email.message}</p>
              ) : slotLabels.length > 0 ? (
                slotLabels.map((label) => (
                  <p key={label} data-testid="invite-slot-taken" className="text-meta text-destructive">
                    {label}
                  </p>
                ))
              ) : (
                <p className="text-meta text-muted-foreground">{t('clients.addClientDrawer.emailHelper')}</p>
              )}
            </div>
          </div>
        </section>

        {isDualRole && (
          <section className="flex flex-col gap-3">
            <SectionTitle>{t('clients.addClientDrawer.sections.scope')}</SectionTitle>
            <div role="group" aria-label={t('clients.addClientDrawer.sections.scope')} className="grid grid-cols-3 gap-2">
              {SCOPE_OPTIONS.map((option) => {
                const isSelected = scope === option.value;
                return (
                  <button
                    key={option.value}
                    type="button"
                    aria-pressed={isSelected}
                    onClick={() => setScope(option.value)}
                    className={cn(
                      'inline-flex h-10 items-center justify-center gap-1.5 rounded-field border px-3 text-body font-semibold outline-none focus-visible:ring-3 focus-visible:ring-ring/50',
                      isSelected
                        ? 'border-nutrition bg-nutrition-soft text-nutrition-ink'
                        : 'border-border bg-background text-foreground hover:bg-muted',
                    )}
                  >
                    {isSelected && <Check className="size-4" aria-hidden="true" />}
                    {t(option.labelKey)}
                  </button>
                );
              })}
            </div>
            <p className="text-meta text-muted-foreground">{t('clients.addClientDrawer.scopeHelper')}</p>
          </section>
        )}

        <section data-testid="invite-offer" className="flex flex-col gap-3">
          <SectionTitle>{t('clients.addClientDrawer.sections.offer')}</SectionTitle>
          <div className="flex flex-col gap-3 rounded-2xl bg-sunken p-4">
            {!notEntitled && (
              <>
                <span className="text-body font-semibold text-ink">{t('offer.package')}</span>

                {packagesQuery.isPending ? (
                  <Skeleton className="h-28 w-full" />
                ) : packagesQuery.isError ? (
                  <p className="text-body text-destructive">{t('offer.loadError')}</p>
                ) : packages.length === 0 ? (
                  <p className="text-body text-muted-foreground">
                    {t('offer.noPackages')}{' '}
                    <Link to="/packages" className="font-semibold text-ink underline underline-offset-2">
                      {t('offer.createPackage')}
                    </Link>
                  </p>
                ) : (
                  <PackageTiles
                    packages={packages}
                    selectedId={offeredPackage?.id ?? NO_PACKAGE_ID}
                    onSelect={onPackageSelect}
                    allowNone
                  />
                )}

                {offeredPackage && (
                  <DiscountPicker
                    discounts={discounts}
                    pkg={offeredPackage}
                    value={offeredDiscount?.discountId ?? ''}
                    onChange={setDiscountChoice}
                  />
                )}

                {packagesQuery.isSuccess && packages.length > 0 && (
                  <p className="text-meta text-muted-foreground">
                    {offeredPackage ? t('clients.addClientDrawer.billingHint') : t('clients.addClientDrawer.noPackageHint')}
                  </p>
                )}
              </>
            )}

            <OnboardingFormRow
              forms={forms}
              value={formId}
              onChange={setFormChoice}
              isLoading={formsQuery.isPending}
              isError={formsQuery.isError}
            />
          </div>
        </section>

        <OfferMessage value={message} onChange={setMessage} />
      </div>

      <div className="flex items-center gap-3 border-t border-border px-6 py-4">
        <Button type="button" variant="ghost" size="lg" className="px-2 font-semibold" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <span className="ml-auto" />
        {offeredPackage && <OfferPayable pkg={offeredPackage} price={offerPrice(offeredPackage, offeredDiscount)} />}
        <Button
          type="submit"
          size="lg"
          className="gap-1.75 px-3.5"
          disabled={!isValid || messageTooLong || listsLoading || formsQuery.isPending || inviteMutation.isPending}
        >
          <Send aria-hidden="true" />
          {inviteMutation.isPending ? t('common.sending') : t('clients.addClientDrawer.submit')}
        </Button>
      </div>
    </form>
    <ClientLimitDialog open={capOpen} onOpenChange={setCapOpen} context="invite" />
    </>
  );
}
