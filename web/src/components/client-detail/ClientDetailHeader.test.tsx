/**
 * The client-detail header shows the client's avatar: the photo when the dashboard carries one,
 * the initials otherwise.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import type { GetClientOfflineSyncConflictsResponse } from '@/api/generated';
import type { ClientTimelineResponse } from '@/api/timeline';
import {
  CLIENT_ID,
  clientDashboard,
  newClientDetailState,
  renderOverviewTab,
  serveClientDetail,
} from '@/test/clientDetail';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const PHOTO_URL = 'https://blob.example.test/avatars/client.jpg';

describe('ClientDetailHeader avatar', () => {
  const originalImage = window.Image;

  beforeEach(() => {
    // Radix AvatarImage mounts its <img> only once window.Image reports a loaded image, which jsdom never does.
    class LoadedImage {
      complete = true;
      naturalWidth = 1;
      referrerPolicy = '';
      crossOrigin: string | null = null;
      src = '';
      addEventListener() {}
      removeEventListener() {}
    }
    vi.stubGlobal('Image', LoadedImage);
    const timeline: ClientTimelineResponse = { items: [] };
    const conflicts: GetClientOfflineSyncConflictsResponse = {};
    server.use(
      http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/timeline`), () => HttpResponse.json(timeline)),
      http.get(apiUrl(`/trainer/clients/${CLIENT_ID}/offline-sync-conflicts`), () => HttpResponse.json(conflicts)),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    window.Image = originalImage;
  });

  it('shows the initials when the client has no photo', async () => {
    const state = newClientDetailState({ dashboard: clientDashboard({ firstName: 'Jana', lastName: 'Novak' }) });
    serveClientDetail(state);
    renderOverviewTab();

    await screen.findByRole('heading', { name: /Jana Novak/ });
    const avatar = screen.getByTestId('client-avatar');

    expect(within(avatar).getByText('JN')).toBeInTheDocument();
    expect(avatar.querySelector('img')).toBeNull();
  });

  it('shows the photo when the dashboard has an avatar URL', async () => {
    const state = newClientDetailState({ dashboard: clientDashboard({ avatarBlobUrl: PHOTO_URL }) });
    serveClientDetail(state);
    renderOverviewTab();

    await screen.findByRole('heading', { name: /QA Client/ });
    const avatar = screen.getByTestId('client-avatar');

    await vi.waitFor(() => expect(avatar.querySelector('img')).toHaveAttribute('src', PHOTO_URL));
  });
});
