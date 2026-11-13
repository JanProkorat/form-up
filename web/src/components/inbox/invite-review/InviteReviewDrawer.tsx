import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Check, Undo2, Upload } from 'lucide-react';
import ClientAvatar from '@/components/clients/ClientAvatar';
import AboutSection from '@/components/inbox/invite-review/AboutSection';
import ChosenPackageCard from '@/components/offer/ChosenPackageCard';
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
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { discountValueLabel } from '@/components/client-detail/payment/paymentModel';
import { isNotEntitled, isRequestStateConflict } from '@/components/client-detail/payment/paymentErrors';
import { ClientRequestStatus } from '@/api/client-requests';
import type { IncomingClientRequestDto } from '@/api/client-requests';
import { QuestionnaireType } from '@/api/generated';
import {
  useAcceptClientRequest,
  useIncomingRequests,
  useProposeClientRequestPackage,
  useRejectClientRequest,
  useWithdrawClientRequestProposal,
} from '@/hooks/useClientsQueries';
import { usePaymentDiscounts, usePaymentPackages } from '@/hooks/useClientPaymentQueries';
import { useTrainerQuestionnaires } from '@/hooks/useQuestionnaireQueries';
import ClientLimitDialog from '@/components/subscription/ClientLimitDialog';
import { firstName, formatSentWhen, useHeldServices } from '@/lib/cooperationCards';
import { isClientLimitReached } from '@/lib/subscriptionErrors';

interface Props {
  /** Public id of the incoming client request to review. */
  requestId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

/**
 * The coach's review of a client request: what the client sent, what the coach offers (package, discount, onboarding
 * form, message) and one primary action. Reads the request from the incoming-requests list, so it stays current when
 * the list refetches (realtime, or after an action).
 */
export default function InviteReviewDrawer({ requestId, open, onOpenChange }: Props) {
  const { t } = useTranslation();
  const requestsQuery = useIncomingRequests(open);
  const request = requestsQuery.data?.find((candidate) => candidate.publicId === requestId);

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent
        data-testid="invite-review-drawer"
        className="w-full gap-0 p-0 sm:w-drawer sm:max-w-none"
        // The first field would take focus before the packages load; the coach starts from the offer, not the message.
        onOpenAutoFocus={(event) => event.preventDefault()}
      >
        {request ? (
          <ReviewBody key={request.publicId} request={request} onClose={() => onOpenChange(false)} />
        ) : (
          <>
            <SheetHeader className="border-b border-border px-6 py-5">
              <SheetTitle className="font-display text-card-title">{t('inviteReview.title')}</SheetTitle>
              <SheetDescription className="sr-only">{t('inviteReview.description')}</SheetDescription>
            </SheetHeader>
            <div className="flex flex-1 flex-col gap-3 px-6 py-5">
              {requestsQuery.isPending ? (
                <Skeleton className="h-40 w-full" />
              ) : (
                <>
                  <p data-testid="invite-review-unavailable" className="text-body text-muted-foreground">
                    {requestsQuery.isError ? t('common.dataLoadError') : t('inviteReview.unavailable')}
                  </p>
                  {requestsQuery.isError && (
                    <Button type="button" variant="outline" size="sm" className="w-fit" onClick={() => void requestsQuery.refetch()}>
                      {t('clients.retry')}
                    </Button>
                  )}
                </>
              )}
            </div>
          </>
        )}
      </SheetContent>
    </Sheet>
  );
}

function ReviewBody({ request, onClose }: { request: IncomingClientRequestDto; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const services = useHeldServices();
  const publicId = request.publicId ?? '';
  const clientFirstName = request.clientFirstName ?? '';
  const clientName = [request.clientFirstName, request.clientLastName].filter(Boolean).join(' ') || (request.clientEmail ?? '');
  const shortName = firstName(clientName);

  const proposal = request.status === ClientRequestStatus.PackageProposed ? request.proposal : undefined;
  const isWaiting = request.status === ClientRequestStatus.PackageProposed;
  const clientPackage = request.package ? toOfferPackage(request.package) : undefined;
  const hasOffer = !isWaiting;
  const isProposeVariant = hasOffer && clientPackage === undefined;

  // Packages and discounts only matter while the coach can still shape an offer.
  const packagesQuery = usePaymentPackages(isProposeVariant);
  const discountsQuery = usePaymentDiscounts(hasOffer && (clientPackage !== undefined || packagesQuery.isSuccess));
  const notEntitled =
    (packagesQuery.isError && isNotEntitled(packagesQuery.error)) ||
    (discountsQuery.isError && isNotEntitled(discountsQuery.error));
  const coachPackages: OfferPackage[] = (packagesQuery.data ?? []).map(toOfferPackage);
  const discounts = discountsQuery.data ?? [];

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

  // Untouched choices (null) follow the defaults, which are only known once the lists have loaded.
  const [packageChoice, setPackageChoice] = useState<string | null>(null);
  const [discountChoice, setDiscountChoice] = useState('');
  const [formChoice, setFormChoice] = useState<string | null>(null);
  const [message, setMessage] = useState('');
  const [capOpen, setCapOpen] = useState(false);

  const selectedPackageId = packageChoice ?? coachPackages[0]?.id ?? NO_PACKAGE_ID;
  const offeredPackage = isProposeVariant
    ? coachPackages.find((pkg) => pkg.id === selectedPackageId)
    : clientPackage;
  // A discount that stopped fitting (another package, archived meanwhile) drops out of the offer.
  const offeredDiscount = usableDiscounts(discounts).find(
    (discount) => discount.discountId === discountChoice && discountBlock(discount, offeredPackage) === null,
  );
  const formId = formChoice ?? defaultFormId;

  const acceptMutation = useAcceptClientRequest();
  const proposeMutation = useProposeClientRequestPackage();
  const rejectMutation = useRejectClientRequest();
  const withdrawMutation = useWithdrawClientRequestProposal();
  const isBusy =
    acceptMutation.isPending || proposeMutation.isPending || rejectMutation.isPending || withdrawMutation.isPending;
  const messageTooLong = message.length > OFFER_MESSAGE_MAX_LENGTH;

  // The server answers 409 CLIENT_REQUEST_INVALID_STATE when the request moved on elsewhere; the drawer has nothing left to do.
  const closeOnConflict = {
    onError: (error: unknown) => {
      if (isClientLimitReached(error)) {
        setCapOpen(true);
        return;
      }
      if (isRequestStateConflict(error)) {
        onClose();
      }
    },
  };
  const statement = message.trim() || undefined;
  // Skip is sent only when the coach explicitly picked "None"; an untouched row never skips.
  const skipOnboardingForm = formChoice === NO_FORM_ID;
  const questionnairePublicId = skipOnboardingForm ? null : formChoice ?? (defaultFormId === NO_FORM_ID ? null : defaultFormId);

  function accept() {
    acceptMutation.mutate(
      {
        publicId,
        questionnairePublicId,
        skipOnboardingForm,
        statement,
        discountPublicId: clientPackage && offeredDiscount ? offeredDiscount.discountId : null,
      },
      { onSuccess: onClose, ...closeOnConflict },
    );
  }

  function propose() {
    if (!offeredPackage) {
      return;
    }
    proposeMutation.mutate(
      {
        publicId,
        packagePublicId: offeredPackage.id,
        discountPublicId: offeredDiscount?.discountId,
        questionnairePublicId: questionnairePublicId ?? undefined,
        skipOnboardingForm,
        statement,
      },
      { onSuccess: onClose, ...closeOnConflict },
    );
  }

  const primaryIsPropose = isProposeVariant && offeredPackage !== undefined;
  const primaryLabel = isBusy
    ? t('common.saving')
    : primaryIsPropose
      ? t('inviteReview.proposePackage')
      : clientPackage
        ? t('inviteReview.accept')
        : t('inviteReview.acceptWithoutPackage');

  const wants = services.length === 1 ? t(`inviteReview.wants.${services[0]}`) : t('inviteReview.wants.both');
  const sent = request.sentAt ? formatSentWhen(t, request.sentAt, i18n.language) : '';

  return (
    <>
      <SheetHeader className="flex-row items-center gap-3.5 border-b border-border px-6 py-5">
        <ClientAvatar
          firstName={request.clientFirstName}
          lastName={request.clientLastName}
          className="size-12 border border-border"
          fallbackClassName="bg-card text-body font-semibold text-ink"
        />
        <div className="flex min-w-0 flex-col gap-1.5">
          <SheetTitle className="truncate font-display text-card-title">{clientName}</SheetTitle>
          <SheetDescription className="flex flex-wrap items-center gap-2 text-body">
            <span className="rounded-full bg-training-soft px-2.25 py-0.75 text-meta font-semibold text-training-ink">
              {wants}
            </span>
            <span>{sent ? t('inviteReview.sent', { when: sent }) : t('inviteReview.invite')}</span>
          </SheetDescription>
        </div>
      </SheetHeader>

      <div className="flex flex-1 flex-col gap-6 overflow-y-auto px-6 py-5">
        <AboutSection request={request} clientName={clientFirstName || clientName} />

        {isWaiting ? (
          <section data-testid="invite-review-waiting" className="flex flex-col gap-3">
            <h3 className="text-label font-bold tracking-label text-muted-foreground uppercase">
              {t('inviteReview.yourOffer')}
            </h3>
            {proposal?.package && <ChosenPackageCard pkg={toOfferPackage(proposal.package)} />}
            {proposal?.discount && (
              <p className="text-body text-ink">
                {proposal.discount.name}{' '}
                {discountValueLabel(
                  proposal.discount.type,
                  proposal.discount.value,
                  proposal.discount.currency,
                  i18n.language,
                )}
              </p>
            )}
            <p className="flex items-center gap-2 rounded-field bg-sunken px-3.5 py-2.5 text-body text-ink">
              <span aria-hidden="true" className="size-1.5 rounded-full bg-marker-solid" />
              {t('inviteReview.waitingFor', { name: shortName })}
            </p>
          </section>
        ) : (
          <>
            <section data-testid="invite-review-offer" className="flex flex-col gap-3">
              <h3 className="text-label font-bold tracking-label text-muted-foreground uppercase">
                {t('inviteReview.yourOffer')}
              </h3>
              <div className="flex flex-col gap-3 rounded-2xl bg-sunken p-4">
                <span className="text-body font-semibold text-ink">{t('offer.package')}</span>

                {clientPackage ? (
                  <ChosenPackageCard pkg={clientPackage} badge={t('inviteReview.clientChoice', { name: shortName })} />
                ) : notEntitled ? (
                  <p className="text-body text-muted-foreground">{t('offer.errors.notEntitled')}</p>
                ) : packagesQuery.isPending ? (
                  <Skeleton className="h-28 w-full" />
                ) : packagesQuery.isError ? (
                  <p className="text-body text-destructive">{t('offer.loadError')}</p>
                ) : coachPackages.length === 0 ? (
                  <p className="text-body text-muted-foreground">
                    {t('offer.noPackages')}{' '}
                    <Link to="/packages" className="font-semibold text-ink underline underline-offset-2">
                      {t('offer.createPackage')}
                    </Link>
                  </p>
                ) : (
                  <PackageTiles
                    packages={coachPackages}
                    selectedId={selectedPackageId}
                    onSelect={setPackageChoice}
                    allowNone
                  />
                )}

                {!notEntitled && offeredPackage && (
                  <DiscountPicker
                    discounts={discounts}
                    pkg={offeredPackage}
                    value={offeredDiscount?.discountId ?? ''}
                    onChange={setDiscountChoice}
                  />
                )}

                <p className="text-meta text-muted-foreground">
                  {clientPackage
                    ? t('inviteReview.billingStarts')
                    : offeredPackage
                      ? t('inviteReview.proposeHint', { name: shortName })
                      : t('inviteReview.noPackageHint', { name: shortName })}
                </p>

                <OnboardingFormRow
                  forms={forms}
                  value={formId}
                  onChange={setFormChoice}
                  clientName={shortName}
                  isLoading={formsQuery.isPending}
                  isError={formsQuery.isError}
                />
              </div>
            </section>

            <OfferMessage value={message} onChange={setMessage} clientName={shortName} />
          </>
        )}
      </div>

      <div className="flex items-center gap-3 border-t border-border px-6 py-4">
        {/* A waiting proposal can only be withdrawn; the server rejects a decline for anything but Pending. */}
        {!isWaiting && (
          <Button
            type="button"
            variant="ghost"
            size="lg"
            className="px-2 font-semibold"
            disabled={isBusy}
            onClick={() => rejectMutation.mutate({ publicId }, { onSuccess: onClose, ...closeOnConflict })}
          >
            {rejectMutation.isPending ? t('common.saving') : t('inviteReview.decline')}
          </Button>
        )}
        <span className="ml-auto" />
        {isWaiting ? (
          <Button
            type="button"
            size="lg"
            className="gap-1.75 px-3.5"
            disabled={isBusy}
            onClick={() => withdrawMutation.mutate(publicId, { onSuccess: onClose, ...closeOnConflict })}
          >
            <Undo2 aria-hidden="true" />
            {withdrawMutation.isPending ? t('common.saving') : t('inviteReview.withdraw')}
          </Button>
        ) : (
          <>
            {offeredPackage && <OfferPayable pkg={offeredPackage} price={offerPrice(offeredPackage, offeredDiscount)} clientName={shortName} />}
            <Button
              type="button"
              size="lg"
              className="gap-1.75 px-3.5"
              disabled={
                isBusy ||
                messageTooLong ||
                formsQuery.isPending ||
                (isProposeVariant && packagesQuery.isPending && !notEntitled)
              }
              onClick={primaryIsPropose ? propose : accept}
            >
              {!isBusy && (primaryIsPropose ? <Upload aria-hidden="true" /> : <Check aria-hidden="true" />)}
              {primaryLabel}
            </Button>
          </>
        )}
      </div>
      <ClientLimitDialog open={capOpen} onOpenChange={setCapOpen} context="accept" clientName={shortName} />
    </>
  );
}
