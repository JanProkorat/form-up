import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Lock, Plus, Search } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { type ClientTabCounts } from '@/api/generated';
import { useClientListParams, type ClientsPageTab } from '@/hooks/useClientListParams';
import { useClients, usePendingClients } from '@/hooks/useClientsQueries';
import { useDebouncedValue } from '@/hooks/useDebouncedValue';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import OutsidePlanBanner from '@/components/subscription/OutsidePlanBanner';
import { cn } from '@/lib/utils';
import ClientFilterMenu, { ClientActiveFilters } from '@/components/clients/ClientFilterMenu';
import ClientTagFilterPopover from '@/components/clients/ClientTagFilterPopover';
import ClientsTable from '@/components/clients/ClientsTable';
import PendingTable from '@/components/clients/PendingTable';
import ClientsPagination from '@/components/clients/ClientsPagination';
import ClientSelectionBar from '@/components/clients/ClientSelectionBar';
import BroadcastDrawer from '@/components/clients/BroadcastDrawer';
import AddClientDrawer from '@/components/clients/AddClientDrawer';

// Order of the PageClients board: All, Active (the default), Outside plan (only
// when it has clients or is open), Pending, Archived.
const TAB_ORDER: ClientsPageTab[] = ['All', 'Active', 'OutsidePlan', 'Pending', 'Archived'];

const TAB_LABEL_KEY: Record<ClientsPageTab, string> = {
  All: 'clients.tabs.all',
  Active: 'clients.tabs.active',
  OutsidePlan: 'clients.tabs.outsidePlan',
  Pending: 'clients.tabs.pending',
  Archived: 'clients.tabs.archived',
};

const TAB_COUNT_KEY: Record<ClientsPageTab, keyof ClientTabCounts> = {
  All: 'all',
  Active: 'inPlan',
  OutsidePlan: 'outsidePlan',
  Pending: 'pending',
  Archived: 'archived',
};

/**
 * The trainer-portal Clients page — tabs, search, tag and filter menus,
 * the client table, and pagination. See PLAN-1066-clients-page.md §2-3 for
 * the phased build (this is phase 3) and the design decisions behind the
 * URL-as-state-of-record model consumed via `useClientListParams`.
 */
export default function ClientsPage() {
  const { t } = useTranslation();
  const { filters, setSearch, setTab, setPlan, setTagIds, setPage, clearPlan, clearFilters } = useClientListParams();

  const [searchInput, setSearchInput] = useState(filters.search);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [broadcastOpen, setBroadcastOpen] = useState(false);
  const [addClientOpen, setAddClientOpen] = useState(false);

  // Selection is page-local UI state, not a URL filter — clear it whenever
  // the tab changes so a stale selection can't keep the bulk bar alive over
  // rows that can no longer receive a broadcast (e.g. switching to Pending,
  // which has no selection at all). Adjusted during render, same pattern as
  // `syncedSearch` above — React's own "adjusting state when a prop changes"
  // pattern — rather than a useEffect, which would call setState after an
  // extra commit and trigger a second, avoidable render.
  const [selectionTab, setSelectionTab] = useState(filters.tab);
  if (filters.tab !== selectionTab) {
    setSelectionTab(filters.tab);
    setSelectedIds(new Set());
  }

  function toggleRow(id: string) {
    setSelectedIds((previous) => {
      const next = new Set(previous);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  function toggleAll(ids: string[]) {
    setSelectedIds((previous) => {
      const allSelected = ids.length > 0 && ids.every((id) => previous.has(id));
      if (allSelected) {
        const next = new Set(previous);
        for (const id of ids) {
          next.delete(id);
        }
        return next;
      }
      return new Set([...previous, ...ids]);
    });
  }

  // Re-sync the local input when the URL's search value changes from
  // elsewhere (browser back/forward, clearFilters, a hand-edited URL).
  // Adjusted during render rather than in a useEffect — React's own
  // "adjusting state when a prop changes" pattern
  // (https://react.dev/learn/you-might-not-need-an-effect) — so a
  // clearFilters click clears the visible box in the same render instead
  // of flashing stale text for one frame.
  const [syncedSearch, setSyncedSearch] = useState(filters.search);
  if (filters.search !== syncedSearch) {
    setSyncedSearch(filters.search);
    setSearchInput(filters.search);
  }

  // Fires 300ms after the last keystroke settles; `setSearch` itself resets
  // page to 1 and uses `replace` so Back doesn't walk one step per keystroke.
  //
  // The equality guard is load-bearing, not a micro-optimisation: the debounce
  // effect runs once on mount regardless of whether the value changed, and
  // `setSearch` clears `page`. Without it, opening /clients?page=2 from a
  // bookmark or a shared link silently snapped back to page 1 after 300ms.
  useDebouncedValue(searchInput, 300, () => {
    if (searchInput !== filters.search) {
      setSearch(searchInput);
    }
  });

  const isPendingTab = filters.tab === 'Pending';

  // Deliberately enabled on every tab, including Pending — tab and plan counts
  // live only on this response (see useClients' own doc comment). Do not
  // gate this query on `!isPendingTab`.
  const clientsQuery = useClients(filters);
  const pendingQuery = usePendingClients();

  // Search/plan/tag changes don't clear the selection outright — they PRUNE
  // it: drop any selected client the new filter excludes, keep the rest (a
  // coach who ticks 10 and then narrows the list to 7 should still see 7
  // selected). Paging within the same filter must NOT prune — `setPage`
  // never touches `search`/`plan`/`tags`, so it never changes `filterKey`
  // and this block never runs for it. `tagIds` is sorted to match
  // `useClients`' own `tagIdsKey`, so re-picking the same tags in a
  // different click order isn't treated as a filter change.
  //
  // We only ever have ONE page of the new filtered result (every filter
  // setter also resets `page` to 1 — see useClientListParams), never the
  // full result set, so a selected client who does survive the new filter
  // but sits on page 2+ looks indistinguishable here from one the filter
  // actually excludes. We deliberately resolve that ambiguity by dropping
  // them (over-pruning): a shrunk selection is recoverable and visible in
  // the bulk bar's count, whereas broadcasting to someone the coach can no
  // longer see in the list is not. Getting this exact (matching the whole
  // filtered set, not just page 1) would need a second request per filter
  // change purely to re-validate the selection — not worth it here.
  //
  // Both halves of the guard are load-bearing, and `isPlaceholderData` alone
  // is NOT enough:
  //
  //   `!isPlaceholderData` — while it is true, `clientsQuery.data` is still
  //   the PREVIOUS filter's page (`keepPreviousData`), so pruning then would
  //   prune against the wrong set entirely.
  //
  //   `isSuccess` — `isPlaceholderData` is only ever true while the query is
  //   pending (query-core sets it inside a `status === 'pending'` branch), so
  //   a FAILED fetch reports `isPlaceholderData: false` with `data`
  //   undefined. Without this half, a filter change whose request errors
  //   makes `visibleIds` empty and wipes the whole selection — not the
  //   conservative over-prune argued above, but total loss, and worse than
  //   the pre-pruning behaviour that kept everything. The table renders a
  //   Retry button, so a failed fetch is a routine path, not a corner.
  //
  // `isSuccess` rather than `!isError` because it also covers the first-ever
  // load, where `keepPreviousData` has no previous page to serve and the
  // query sits pending with `data` undefined. And because the marker only
  // advances inside the block, a successful Retry still prunes against real
  // data — gating the read while advancing the marker would skip the prune
  // forever after one failure.
  //
  // Adjusted during render, same pattern as `selectionTab`/`syncedSearch`.
  const filterKey = `${filters.search}|${filters.plan ?? ''}|${[...filters.tagIds].sort().join(',')}`;
  const [prunedFilterKey, setPrunedFilterKey] = useState(filterKey);
  if (filterKey !== prunedFilterKey && clientsQuery.isSuccess && !clientsQuery.isPlaceholderData) {
    setPrunedFilterKey(filterKey);
    const visibleIds = new Set((clientsQuery.data?.clients ?? []).map((client) => client.publicId ?? ''));
    setSelectedIds((previous) => {
      const next = new Set([...previous].filter((id) => visibleIds.has(id)));
      return next.size === previous.size ? previous : next;
    });
  }

  // The All tab lists the waiting invites and requests under the clients; with none waiting it shows no section.
  const showPendingSection =
    filters.tab === 'All' && (pendingQuery.isPending || pendingQuery.isError || (pendingQuery.data ?? []).length > 0);
  const tabCounts = clientsQuery.data?.tabCounts;
  const subscription = useSubscription();
  const showOutsidePlanBanner = subscription.isCoach && subscription.overCapClientCount > 0;
  const hasPlanFilter = filters.plan !== null;
  const hasActiveFilter = filters.search !== '' || hasPlanFilter || filters.tagIds.length > 0;
  const isOutsidePlanTab = filters.tab === 'OutsidePlan';
  const visibleTabs = TAB_ORDER.filter(
    (tab) => tab !== 'OutsidePlan' || isOutsidePlanTab || (tabCounts?.outsidePlan ?? 0) > 0,
  );

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="flex flex-col gap-1.5">
          <span className="flex items-center gap-1.75 text-label font-semibold tracking-label text-marker uppercase">
            <span className="size-1.75 rounded-full bg-marker" aria-hidden="true" />
            {t('clients.eyebrow')}
          </span>
          <h1 className="text-title font-bold text-ink">{t('clients.title')}</h1>
          <p className="text-body text-muted-foreground">{t('clients.subtitle')}</p>
        </div>
        <Button type="button" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={() => setAddClientOpen(true)}>
          <Plus aria-hidden="true" />
          {t('clients.inviteClient')}
        </Button>
      </div>

      {showOutsidePlanBanner && (
        <OutsidePlanBanner
          planName={subscription.planName}
          clientLimit={subscription.clientLimit}
          overCapClientCount={subscription.overCapClientCount}
          freeSlotCount={subscription.freeSlotCount}
          onShowClients={isOutsidePlanTab ? undefined : () => setTab('OutsidePlan')}
        />
      )}

      <Tabs value={filters.tab} onValueChange={(value) => setTab(value as ClientsPageTab)}>
        {/* Below the width where all four tabs fit (phone-width shell fix,
            see PLAN-1066-clients-page.md phase 5), the row scrolls within
            its own container instead of being clipped by the shell's
            `overflow-x-hidden` main region — a clipped-but-not-scrollable
            "Pending" tab would be unreachable rather than merely narrow. */}
        <div className="overflow-x-auto">
          <TabsList variant="underline">
            {visibleTabs.map((tab) => {
              const isActiveTab = filters.tab === tab;
              return (
                <TabsTrigger key={tab} value={tab} variant="underline" className="gap-1.5 text-body">
                  {tab === 'OutsidePlan' && <Lock className="size-3.5" aria-hidden="true" />}
                  {t(TAB_LABEL_KEY[tab])}
                  {tabCounts && (
                    <span
                      className={cn(
                        'inline-flex min-w-badge-min items-center justify-center rounded-full px-1.5 py-px text-label font-bold',
                        isActiveTab ? 'bg-primary text-primary-foreground' : 'bg-line text-muted-foreground',
                      )}
                    >
                      {tabCounts[TAB_COUNT_KEY[tab]] ?? 0}
                    </span>
                  )}
                </TabsTrigger>
              );
            })}
          </TabsList>
        </div>
      </Tabs>

      {!isPendingTab && (
        <div className="flex flex-wrap items-center gap-3">
          <div className="relative w-full max-w-search">
              <Search
                className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
                aria-hidden="true"
              />
              <Input
                type="search"
                value={searchInput}
                onChange={(event) => setSearchInput(event.target.value)}
                placeholder={t('clients.searchPlaceholder')}
                className="h-9 pl-9"
                aria-label={t('clients.searchPlaceholder')}
              />
          </div>
          <ClientTagFilterPopover selectedTagIds={filters.tagIds} onChange={setTagIds} />
          <ClientFilterMenu
            plan={filters.plan}
            counts={clientsQuery.data?.planCounts}
            onChange={setPlan}
          />
        </div>
      )}
      {!isPendingTab && hasPlanFilter && <ClientActiveFilters plan={filters.plan} onRemove={clearPlan} />}

      {isPendingTab ? (
        <PendingTable
          rows={pendingQuery.data ?? []}
          isPending={pendingQuery.isPending}
          isError={pendingQuery.isError}
          onRetry={() => void pendingQuery.refetch()}
        />
      ) : (
        <>
        <div className="overflow-hidden rounded-2xl border border-border bg-card shadow-selection-bar">
          <ClientsTable
            clients={clientsQuery.data?.clients ?? []}
            isPending={clientsQuery.isPending}
            isError={clientsQuery.isError}
            onRetry={() => void clientsQuery.refetch()}
            hasActiveFilter={hasActiveFilter}
            onClearFilters={clearFilters}
            onInviteClient={() => setAddClientOpen(true)}
            selectedIds={selectedIds}
            onToggleRow={toggleRow}
            onToggleAll={toggleAll}
          />
          <ClientsPagination
            rowCount={(clientsQuery.data?.clients ?? []).length}
            page={filters.page}
            pageSize={filters.pageSize}
            totalCount={clientsQuery.data?.totalCount ?? 0}
            onPageChange={setPage}
          />
        </div>
        {showPendingSection && (
          <section aria-labelledby="clients-waiting-heading" className="flex flex-col gap-2">
            <h2 id="clients-waiting-heading" className="text-label font-bold tracking-label text-ink-2 uppercase">
              {t('clients.tabs.pending')}
            </h2>
            <div className="overflow-hidden rounded-2xl border border-border bg-card shadow-selection-bar">
              <PendingTable
                rows={pendingQuery.data ?? []}
                isPending={pendingQuery.isPending}
                isError={pendingQuery.isError}
                onRetry={() => void pendingQuery.refetch()}
              />
            </div>
          </section>
        )}
        </>
      )}

      <ClientSelectionBar
        count={selectedIds.size}
        onBroadcast={() => setBroadcastOpen(true)}
        onCancel={() => setSelectedIds(new Set())}
      />
      <BroadcastDrawer
        open={broadcastOpen}
        onOpenChange={setBroadcastOpen}
        clientPublicIds={[...selectedIds]}
        onSent={() => setSelectedIds(new Set())}
      />
      <AddClientDrawer open={addClientOpen} onOpenChange={setAddClientOpen} />
    </div>
  );
}
