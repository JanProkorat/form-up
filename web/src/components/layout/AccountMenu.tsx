import { useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useActiveRoles } from '@/hooks/useActiveRoles';
import { ChevronUp, CreditCard, LogOut, Monitor, Moon, Settings, Sun, User, Users } from 'lucide-react';
import SidebarPlanRow from '@/components/subscription/SidebarPlanRow';
import { KEEP_CLIENTS_PATH, PLAN_BILLING_PATH } from '@/components/subscription/planLinks';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import { cn } from '@/lib/utils';
import { useAuthStore } from '@/stores/auth';
import { useThemeStore, type ThemePreference } from '@/stores/theme';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';

const LANGUAGES = ['cs', 'en', 'de'] as const;
const THEMES: { value: ThemePreference; Icon: typeof Sun }[] = [
  { value: 'light', Icon: Sun },
  { value: 'dark', Icon: Moon },
  { value: 'system', Icon: Monitor },
];

/**
 * Fixed precedence for the signed-in user's role line — never `roles[0]`,
 * the array order carries no contract. A dual-role professional legitimately
 * holds more than one of these at once; every held role is shown, joined.
 * admin/client come from the JWT roles, trainer/nutritionist from the active
 * coach roles (GET /users/me/roles), since the JWT keeps removed coach roles.
 */
const ROLE_ORDER = ['admin', 'trainer', 'nutritionist', 'client'] as const;

const COACH_ROLES = new Set(['trainer', 'nutritionist']);

function initialsOf(firstName: string, lastName: string): string {
  return `${firstName[0] ?? ''}${lastName[0] ?? ''}`.toUpperCase() || '?';
}

function formatRoleLabel(roles: string[], t: (key: string) => string): string {
  const held = new Set(roles.map((role) => role.toLowerCase()));
  return ROLE_ORDER.filter((role) => held.has(role))
    .map((role) => t(`roles.${role}`))
    .join(' · ');
}

const ITEM_CLASS =
  'flex h-9.5 w-full items-center gap-2.5 rounded-md px-2.5 text-copy font-medium text-ink outline-none transition-colors hover:bg-sunken focus-visible:bg-sunken focus-visible:ring-2 focus-visible:ring-ring/50';

function Segmented({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div role="group" aria-label={label} className="flex flex-col gap-1.75 px-2.5 py-1">
      <span className="text-meta font-semibold text-muted-foreground">{label}</span>
      <div className="flex gap-0.5 rounded-xl bg-sunken p-0.75">{children}</div>
    </div>
  );
}

function SegmentButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      className={cn(
        'inline-flex h-8 flex-1 items-center justify-center gap-1.25 rounded-md px-1.5 text-meta font-semibold outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring/50',
        active ? 'bg-surface text-ink shadow-selection-bar' : 'text-muted-foreground hover:text-ink',
      )}
    >
      {children}
    </button>
  );
}

interface Props {
  /** Fired when a navigation item is activated — closes the mobile off-canvas drawer. */
  onNavigate?: () => void;
  /** Rail mode: the trigger shrinks to just the avatar. */
  collapsed?: boolean;
}

/** Sidebar user card that opens the account popover (profile, settings, language, theme, log out). */
export default function AccountMenu({ onNavigate, collapsed = false }: Props) {
  const { t, i18n } = useTranslation();
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);
  const preference = useThemeStore((s) => s.preference);
  const setPreference = useThemeStore((s) => s.setPreference);
  const [open, setOpen] = useState(false);

  const activeRoles = useActiveRoles();
  const subscription = useSubscription();
  const tokenRoles = (user?.roles ?? []).map((role) => role.toLowerCase());
  const tokenCoachRoles = tokenRoles.filter((role) => COACH_ROLES.has(role));

  // restoreSession() populates `user` asynchronously — guard against the render pass before it resolves.
  if (!user) return null;

  const initials = initialsOf(user.firstName, user.lastName);
  // Display only: while loading or on error, fall back to the token's coach roles so the line never flickers empty.
  const nonCoachRoles = tokenRoles.filter((role) => !COACH_ROLES.has(role));
  const activeCoachRoles =
    activeRoles.isLoading || activeRoles.isError
      ? tokenCoachRoles
      : activeRoles.roles.map((role) => role.toLowerCase()).filter((role) => COACH_ROLES.has(role));
  const roleLabel = formatRoleLabel([...nonCoachRoles, ...activeCoachRoles], t);
  const close = () => {
    setOpen(false);
    onNavigate?.();
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <div className="flex flex-col rounded-field border border-sidebar-active bg-sidebar-active">
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-label={t('accountMenu.trigger')}
          title={collapsed ? t('accountMenu.trigger') : undefined}
          className={cn(
            'flex w-full items-center rounded-field text-left outline-none transition-opacity hover:opacity-90 focus-visible:ring-2 focus-visible:ring-sidebar-text/60',
            collapsed ? 'justify-center p-1.5' : 'gap-2.5 py-1.75 pr-1.5 pl-2.75',
          )}
        >
          <span
            aria-hidden="true"
            className="flex size-8 shrink-0 items-center justify-center rounded-full bg-sidebar-active text-meta font-semibold text-sidebar-text"
          >
            {initials}
          </span>
          {!collapsed && (
            <>
              <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                <span className="truncate text-meta font-semibold text-sidebar-text">
                  {user.firstName} {user.lastName}
                </span>
                {roleLabel && <span className="text-label leading-snug text-sidebar-muted">{roleLabel}</span>}
              </span>
              <ChevronUp
                className={cn('size-4 shrink-0 text-sidebar-text transition-transform', !open && 'rotate-180')}
                aria-hidden="true"
              />
            </>
          )}
        </button>
      </PopoverTrigger>
      {!collapsed && subscription.isCoach && subscription.data && (
        <SidebarPlanRow
          planName={subscription.planName}
          status={subscription.data.status}
          used={subscription.activeClientCount}
          limit={subscription.clientLimit}
          lapsed={subscription.isLapsed}
          onNavigate={onNavigate}
        />
      )}
      </div>
      <PopoverContent
        side="right"
        align="end"
        sideOffset={14}
        aria-label={t('accountMenu.title')}
        className="flex w-72 flex-col rounded-xl p-2 shadow-popover"
      >
        <div className="flex items-center gap-2.75 px-2.5 pt-2 pb-2.5">
          <span
            aria-hidden="true"
            className="flex size-9 shrink-0 items-center justify-center rounded-full bg-sunken text-meta font-semibold text-ink"
          >
            {initials}
          </span>
          <span className="flex min-w-0 flex-col gap-0.5">
            <span className="truncate text-copy font-semibold text-ink">
              {user.firstName} {user.lastName}
            </span>
            <span className="truncate text-meta text-muted-foreground">{user.email}</span>
          </span>
        </div>
        <div className="my-1.5 h-px bg-border" />
        <Link to="/profile" onClick={close} className={ITEM_CLASS}>
          <User className="size-4 text-muted-foreground" aria-hidden="true" />
          {t('accountMenu.profile')}
        </Link>
        <Link to="/settings" onClick={close} className={ITEM_CLASS}>
          <Settings className="size-4 text-muted-foreground" aria-hidden="true" />
          {t('accountMenu.settings')}
        </Link>
        {subscription.isCoach && (
          <Link to={PLAN_BILLING_PATH} onClick={close} className={ITEM_CLASS}>
            <CreditCard className="size-4 text-muted-foreground" aria-hidden="true" />
            {t('accountMenu.planBilling')}
            {subscription.planName && (
              <span className="ml-auto text-meta font-semibold text-muted-foreground">{subscription.planName}</span>
            )}
          </Link>
        )}
        {subscription.isCoach && subscription.isLapsed && (
          <Link to={KEEP_CLIENTS_PATH} onClick={close} className={ITEM_CLASS}>
            <Users className="size-4 text-muted-foreground" aria-hidden="true" />
            {t('accountMenu.keepClients')}
          </Link>
        )}
        <div className="my-1.5 h-px bg-border" />
        <Segmented label={t('accountMenu.language')}>
          {LANGUAGES.map((lng) => (
            <SegmentButton
              key={lng}
              active={i18n.resolvedLanguage === lng}
              onClick={() => {
                void i18n.changeLanguage(lng);
              }}
            >
              {t(`accountMenu.languages.${lng}`)}
            </SegmentButton>
          ))}
        </Segmented>
        <Segmented label={t('accountMenu.theme')}>
          {THEMES.map(({ value, Icon }) => (
            <SegmentButton key={value} active={preference === value} onClick={() => setPreference(value)}>
              <Icon className="size-3.5" aria-hidden="true" />
              {t(`accountMenu.themes.${value}`)}
            </SegmentButton>
          ))}
        </Segmented>
        <div className="my-1.5 h-px bg-border" />
        <button
          type="button"
          onClick={() => {
            setOpen(false);
            logout();
          }}
          className={cn(ITEM_CLASS, 'text-error')}
        >
          <LogOut className="size-4" aria-hidden="true" />
          {t('auth.logout')}
        </button>
      </PopoverContent>
    </Popover>
  );
}
