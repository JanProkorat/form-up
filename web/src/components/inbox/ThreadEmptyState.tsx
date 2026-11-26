import { useTranslation } from 'react-i18next';
import LogoMark from '@/components/brand/LogoMark';

/** Centred empty state shown in the thread pane when no conversation is selected. */
export default function ThreadEmptyState() {
  const { t } = useTranslation();

  return (
    <div className="flex flex-1 flex-col items-center bg-background justify-center gap-3 px-6 text-center">
      <div
        className="flex size-12 items-center justify-center rounded-sm border border-dashed border-border"
        aria-hidden="true"
      >
        <LogoMark className="size-7" />
      </div>
      <div className="flex flex-col gap-1">
        <p className="text-body font-medium text-ink">{t('inbox.emptyState.title')}</p>
        <p className="max-w-xs text-caption text-muted-foreground">{t('inbox.emptyState.subtitle')}</p>
      </div>
    </div>
  );
}
