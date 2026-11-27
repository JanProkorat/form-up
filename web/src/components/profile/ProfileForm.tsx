import { useEffect, useRef, useState } from 'react';
import { FormProvider, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useTranslation } from 'react-i18next';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Check } from 'lucide-react';
import type { GetProfessionalProfileResponse, GetProfileResponse } from '@/api/generated';
import {
  getMyProfile,
  profileKeys,
  updateMyProfile,
  updateTrainerProfile,
} from '@/api/profile';
import { useAuthStore } from '@/stores/auth';
import { showApiError, showSuccess } from '@/lib/api-errors';
import { useUnsavedChangesBlocker } from '@/hooks/useUnsavedChangesBlocker';
import { Button } from '@/components/ui/button';
import UnsavedChangesDialog from '@/components/layout/UnsavedChangesDialog';
import PhotoNameSection from '@/components/profile/PhotoNameSection';
import AccountSection from '@/components/profile/AccountSection';
import AboutSection from '@/components/profile/AboutSection';
import ExpertiseSection from '@/components/profile/ExpertiseSection';
import CollaborationSection from '@/components/profile/CollaborationSection';
import LinksSection from '@/components/profile/LinksSection';
import VisibilitySection from '@/components/profile/VisibilitySection';
import ProfilePreviewCard from '@/components/profile/ProfilePreviewCard';
import ProfileHeader from '@/components/profile/ProfileHeader';
import {
  buildDefaultValues,
  buildTrainerPayload,
  createProfileSchema,
  fieldForBackendName,
  fromPriceOf,
  withPendingTag,
  type ProfileFormValues,
} from '@/components/profile/profile-form';

type TagField = 'specializations' | 'languages';

interface Props {
  me: GetProfileResponse;
  trainer: GetProfessionalProfileResponse;
}

/** Which part of the save a baseline key tracks; each is its own request. */
interface Baseline {
  account: string;
  trainer: string;
}

interface SaveResult {
  values: ProfileFormValues;
  failures: { part: keyof Baseline; error: unknown }[];
  accountSaved: boolean;
}

interface BackendFieldError {
  name?: string;
  code?: string;
  reason?: string;
}

function accountKey(values: ProfileFormValues): string {
  return JSON.stringify([values.firstName.trim(), values.lastName.trim(), values.phoneNumber.trim()]);
}

function computeBaseline(values: ProfileFormValues, legacySpecialization: string | undefined): Baseline {
  return {
    account: accountKey(values),
    trainer: JSON.stringify(buildTrainerPayload(values, legacySpecialization)),
  };
}

/** Validator errors as FastEndpoints sends them: `errors: [{ name, reason, code }]`. */
function readBackendErrors(error: unknown): BackendFieldError[] {
  const body = (error as { response?: { data?: { errors?: unknown } } } | null)?.response?.data;
  const direct = (error as { errors?: unknown } | null)?.errors;
  const list = Array.isArray(body?.errors) ? body.errors : Array.isArray(direct) ? direct : [];
  return list as BackendFieldError[];
}

/** The profile editor: form state, save orchestration and the two-column layout. */
export default function ProfileForm({ me, trainer }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const setUser = useAuthStore((s) => s.setUser);

  // Seeded once: the form owns its values after mount, so a background refetch never overwrites typing.
  const [initialValues] = useState(() => buildDefaultValues(me, trainer));
  const baseline = useRef<Baseline>(computeBaseline(initialValues, trainer.specialization));
  const schema = createProfileSchema(t);
  // Read-only and server-derived: it follows the live query, not the form's one-time seed.
  const fromPrice = fromPriceOf(trainer);

  const form = useForm<ProfileFormValues>({
    resolver: zodResolver(schema),
    defaultValues: initialValues,
  });
  const { handleSubmit, reset, setError, setValue, getValues, formState } = form;

  // Text typed into a tag box without Enter lives here so Save can turn it into a tag before validation.
  const [drafts, setDrafts] = useState<Record<TagField, string>>({ specializations: '', languages: '' });
  const setDraft = (field: TagField, value: string) => setDrafts((prev) => ({ ...prev, [field]: value }));
  const hasPendingDraft = drafts.specializations.trim() !== '' || drafts.languages.trim() !== '';
  const isDirty = formState.isDirty || hasPendingDraft;

  // In-app navigation away (sidebar, in-page link, browser back) asks first while dirty; reload is the effect below.
  const { blocker: leaveBlocker } = useUnsavedChangesBlocker(isDirty);

  useEffect(() => {
    if (!isDirty) return;
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [isDirty]);

  function submit(event: React.FormEvent<HTMLFormElement>) {
    for (const field of ['specializations', 'languages'] as const) {
      if (drafts[field].trim() === '') continue;
      setValue(field, withPendingTag(getValues(field), drafts[field]), { shouldDirty: true });
    }
    setDrafts({ specializations: '', languages: '' });
    // Validation runs after the merge, so a too-long pending tag is rejected like a typed one.
    void handleSubmit((values) => saveMutation.mutate(values))(event);
  }

  function applyFailure(error: unknown) {
    let mapped = false;
    for (const entry of readBackendErrors(error)) {
      const field = fieldForBackendName(entry.name);
      const isCertificates = entry.code === 'INVALID_CERTIFICATES' || field === 'certificates';
      const target = isCertificates ? 'certificates' : field;
      if (!target) continue;
      const message = isCertificates
        ? t('profile.page.validation.invalidCertificates')
        : (entry.code ? t(`apiErrors.${entry.code}`, { defaultValue: '' }) : '') ||
          t('profile.page.validation.serverInvalid');
      setError(target, { message });
      mapped = true;
    }
    if (!mapped) showApiError(error, 'profile.saveError');
  }

  const saveMutation = useMutation({
    mutationFn: async (values: ProfileFormValues): Promise<SaveResult> => {
      const next = computeBaseline(values, trainer.specialization);
      const failures: SaveResult['failures'] = [];
      let accountSaved = false;

      if (next.account !== baseline.current.account) {
        try {
          await updateMyProfile({
            firstName: values.firstName.trim(),
            lastName: values.lastName.trim(),
            phoneNumber: values.phoneNumber.trim() === '' ? null : values.phoneNumber.trim(),
          });
          baseline.current = { ...baseline.current, account: next.account };
          accountSaved = true;
        } catch (error) {
          failures.push({ part: 'account', error });
        }
      }
      if (next.trainer !== baseline.current.trainer) {
        try {
          await updateTrainerProfile(buildTrainerPayload(values, trainer.specialization));
          baseline.current = { ...baseline.current, trainer: next.trainer };
        } catch (error) {
          failures.push({ part: 'trainer', error });
        }
      }
      return { values, failures, accountSaved };
    },
    onSuccess: async ({ values, failures, accountSaved }) => {
      if (accountSaved) {
        // The sidebar card reads the auth store, not the query cache.
        try {
          const profile = await getMyProfile();
          const current = useAuthStore.getState().user;
          if (current) {
            setUser({
              ...current,
              firstName: profile.firstName ?? current.firstName,
              lastName: profile.lastName ?? current.lastName,
              avatarBlobUrl: profile.avatarBlobUrl ?? null,
            });
          }
        } catch {
          // The saved values are on the server; the sidebar catches up on the next session restore.
        }
      }
      void queryClient.invalidateQueries({ queryKey: profileKeys.me });
      void queryClient.invalidateQueries({ queryKey: profileKeys.trainer });

      if (failures.length === 0) {
        reset(values);
        showSuccess('profile.page.saved');
        return;
      }
      for (const { error } of failures) applyFailure(error);
    },
    onError: (error) => showApiError(error, 'profile.saveError'),
  });

  return (
    <FormProvider {...form}>
      <form
        noValidate
        onSubmit={submit}
        className="flex w-full flex-col gap-5"
      >
        <ProfileHeader
          actions={
            <>
              <span
                role="status"
                className="flex items-center gap-1.5 text-body text-muted-foreground"
                data-testid="profile-save-state"
              >
                {isDirty ? (
                  t('profile.unsavedChanges')
                ) : (
                  <>
                    <Check className="size-3.5" aria-hidden="true" />
                    {t('profile.page.allSaved')}
                  </>
                )}
              </span>
              <Button
                type="submit"
                size="lg"
                className="px-4 font-semibold aria-disabled:cursor-default aria-disabled:hover:bg-primary"
                aria-disabled={!isDirty || saveMutation.isPending}
                onClick={(event) => {
                  // Clean or saving: keep the primary look but do nothing.
                  if (!isDirty || saveMutation.isPending) event.preventDefault();
                }}
              >
                {saveMutation.isPending ? t('common.saving') : t('common.save')}
              </Button>
            </>
          }
        />
        <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_22rem]">
          <div className="grid min-w-0 gap-4 2xl:grid-cols-2">
            <PhotoNameSection professionalAvatarUrl={trainer.avatarBlobUrl} userAvatarUrl={me.avatarBlobUrl} />
            <AccountSection email={me.email ?? ''} />
            <AboutSection fromPrice={fromPrice} />
            <ExpertiseSection drafts={drafts} onDraftChange={setDraft} />
            <CollaborationSection />
            <LinksSection />
            <div className="min-w-0 2xl:col-span-2">
              <VisibilitySection />
            </div>
          </div>
          <ProfilePreviewCard
            avatarUrl={trainer.avatarBlobUrl ?? me.avatarBlobUrl}
            roles={me.roles ?? []}
            fromPrice={fromPrice}
          />
        </div>
      </form>
      <UnsavedChangesDialog blocker={leaveBlocker} i18nPrefix="profile.page.leave" />
    </FormProvider>
  );
}
