import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ArrowLeft, Lock } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { LibraryVisibility, type NutritionPlanTemplateDetailDto } from '@/api/nutrition-plan-templates';
import PageHeader from '@/components/library/PageHeader';
import PlanEditor from '@/components/plan-editor/PlanEditor';
import CopyMealsDialog from '@/components/plan-templates/CopyMealsDialog';
import { usePlanTemplateEditor } from '@/hooks/usePlanTemplateEditor';
import { usePlanTemplate } from '@/hooks/usePlanTemplatesQueries';
import { getErrorStatus } from '@/lib/api-errors';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import PaywallPanel from '@/components/subscription/PaywallPanel';
import { useAuthStore } from '@/stores/auth';

interface LoadedProps {
  template: NutritionPlanTemplateDetailDto;
  onReload: () => void;
}

/** Hosts the shared plan editor for one loaded template. Mounted once per load, so edits stay local. */
function LoadedTemplateEditor({ template, onReload }: LoadedProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { initial, saveState, save } = usePlanTemplateEditor(template);

  return (
    <PlanEditor
      initial={initial}
      usage={{ count: template.usedBy ?? 0, shared: template.visibility === LibraryVisibility.Public }}
      renderCopyMeals={(slot) => (
        <CopyMealsDialog
          open={slot.open}
          onOpenChange={slot.onOpenChange}
          currentTemplateId={template.templateId}
          onApply={slot.onApply}
        />
      )}
      readOnly={!template.isOwnedByCurrentUser}
      readOnlyNotice={t('planTemplates.editor.readOnly')}
      breadcrumb={{ label: t('planTemplates.title'), onNavigate: () => navigate('/plan-templates') }}
      saveState={saveState}
      onSave={save}
      onReload={onReload}
    />
  );
}

function BackLink() {
  const { t } = useTranslation();
  return (
    <Button asChild variant="ghost" size="sm" className="w-fit gap-1.5 px-0 font-semibold text-ink">
      <Link to="/plan-templates">
        <ArrowLeft className="size-4" aria-hidden="true" />
        {t('planTemplates.title')}
      </Link>
    </Button>
  );
}

/** The plan template editor page: loads the template and hosts the shared editor. */
export default function PlanTemplateEditorPage() {
  const { t } = useTranslation();
  const { templateId } = useParams();
  const roles = useAuthStore((state) => state.user?.roles ?? []);
  const isNutritionist = roles.includes('Nutritionist');
  const { isEntitled } = useSubscription();
  const planAllows = isEntitled('canUsePlanTemplates');
  const templateQuery = usePlanTemplate(templateId, isNutritionist && planAllows);
  const [reloadCount, setReloadCount] = useState(0);

  const status = templateQuery.isError ? getErrorStatus(templateQuery.error) : null;
  const isLocked =
    isNutritionist && (!planAllows || (templateQuery.isError && isSubscriptionRequired(templateQuery.error)));

  if (isLocked) {
    return (
      <div className="flex h-full flex-col gap-4 p-6">
        <PageHeader eyebrow={t('planTemplates.title')} title={t('planTemplates.editor.title')} />
        <PaywallPanel feature="planTemplates" />
      </div>
    );
  }

  if (!isNutritionist || status === 403) {
    return (
      <div className="flex h-full flex-col gap-4 p-6">
        <PageHeader eyebrow={t('planTemplates.title')} title={t('planTemplates.editor.title')} />
        <div className="flex flex-1 flex-col items-center justify-center gap-3 text-center">
          <Lock className="size-8 text-muted-foreground" aria-hidden="true" />
          <p className="text-copy font-bold text-ink">{t('planTemplates.nutritionistsOnly.title')}</p>
          <p className="max-w-search text-body text-muted-foreground">{t('planTemplates.nutritionistsOnly.body')}</p>
        </div>
      </div>
    );
  }

  if (templateQuery.isPending) {
    return (
      <div className="flex h-full flex-col gap-4 p-6">
        <Skeleton className="h-10 w-80" />
        <Skeleton className="h-full w-full rounded-2xl" />
      </div>
    );
  }

  if (templateQuery.isError) {
    return (
      <div className="flex h-full flex-col gap-4 p-6">
        <BackLink />
        {status === 404 ? (
          <div className="flex flex-col items-start gap-1">
            <p className="text-copy font-bold text-ink">{t('planTemplates.editor.notFound.title')}</p>
            <p className="text-body text-muted-foreground">{t('planTemplates.editor.notFound.body')}</p>
          </div>
        ) : (
          <div className="flex flex-col items-start gap-3">
            <p className="text-body text-muted-foreground">{t('common.loadError')}</p>
            <Button type="button" variant="outline" size="sm" onClick={() => void templateQuery.refetch()}>
              {t('planTemplates.retry')}
            </Button>
          </div>
        )}
      </div>
    );
  }

  return (
    <LoadedTemplateEditor
      key={`${templateId}:${reloadCount}`}
      template={templateQuery.data}
      onReload={() => {
        void templateQuery.refetch().then(() => setReloadCount((count) => count + 1));
      }}
    />
  );
}
