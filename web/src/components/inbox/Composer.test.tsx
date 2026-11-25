/**
 * Composer send id: a retry after a failed send reuses the client message id so the server can replay it,
 * while a success or an edited payload gets a fresh id.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import type { SendMessageRequest, SendMessageResponse } from '@/api/generated';
import { newInboxState, renderInbox, serveInbox } from '@/test/inbox';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

/** Serves the inbox and records each send body; the first `failures` sends answer 500, the rest succeed. */
function serveSend(failures: number): SendMessageRequest[] {
  serveInbox(newInboxState());
  const bodies: SendMessageRequest[] = [];
  server.use(
    http.post(apiUrl('/conversations/:id/messages'), async ({ request }) => {
      const body = (await request.json()) as SendMessageRequest;
      bodies.push(body);
      if (bodies.length <= failures) {
        return HttpResponse.json({ title: 'boom' }, { status: 500 });
      }
      const response: SendMessageResponse = {
        id: `sent-${bodies.length}`,
        text: body.text,
        clientMessageId: body.clientMessageId,
      };
      return HttpResponse.json(response);
    }),
  );
  return bodies;
}

async function openComposer() {
  const rendered = renderInbox();
  const box = await screen.findByPlaceholderText('Type your message here...');
  return { ...rendered, box, send: () => screen.getByRole('button', { name: 'Send message' }) };
}

describe('Composer: client message id', () => {
  it('reuses the id when an unchanged message is retried, and uses a fresh one for the next message', async () => {
    const bodies = serveSend(1);
    const { user, box, send } = await openComposer();

    await user.type(box, 'Hello');
    await user.click(send());
    await waitFor(() => expect(bodies).toHaveLength(1));
    await waitFor(() => expect(send()).toBeEnabled());
    await user.click(send());
    await waitFor(() => expect(bodies).toHaveLength(2));

    expect(bodies[0].clientMessageId).toBeTruthy();
    expect(bodies[1].clientMessageId).toBe(bodies[0].clientMessageId);

    await waitFor(() => expect(box).toHaveValue(''));
    await user.type(box, 'Next');
    await user.click(send());
    await waitFor(() => expect(bodies).toHaveLength(3));
    expect(bodies[2].clientMessageId).toBeTruthy();
    expect(bodies[2].clientMessageId).not.toBe(bodies[0].clientMessageId);
  });

  it('uses a fresh id when the text is edited before the retry', async () => {
    const bodies = serveSend(1);
    const { user, box, send } = await openComposer();

    await user.type(box, 'Hello');
    await user.click(send());
    await waitFor(() => expect(bodies).toHaveLength(1));
    await waitFor(() => expect(send()).toBeEnabled());

    await user.type(box, ' there');
    await user.click(send());
    await waitFor(() => expect(bodies).toHaveLength(2));

    expect(bodies[1].text).toBe('Hello there');
    expect(bodies[1].clientMessageId).toBeTruthy();
    expect(bodies[1].clientMessageId).not.toBe(bodies[0].clientMessageId);
  });
});
