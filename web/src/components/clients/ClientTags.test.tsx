/**
 * Client tags on the Clients page (#1119): creating a tag from the filter popover and from a row's picker.
 * Ported from the Playwright spec (trainer/clients). The fake tag endpoint refuses a repeated name with a 409,
 * as the backend does.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { newClientsListState, renderClientsPage, serveClientsList } from '@/test/clientsList';

type User = ReturnType<typeof renderClientsPage>['user'];

const popoverContent = () => document.querySelector<HTMLElement>("[data-slot='popover-content']");
const dialogContent = () => document.querySelector<HTMLElement>("[data-slot='dialog-content']");

async function openPopover(trigger: HTMLElement, user: User) {
  await user.click(trigger);
  await waitFor(() => expect(popoverContent()).toBeInTheDocument());
}

/** Opens the create dialog from the open popover and returns it. */
async function openCreateDialog(user: User) {
  await user.click(within(popoverContent() as HTMLElement).getByRole('button', { name: 'Create tag' }));
  await waitFor(() => expect(dialogContent()).toBeInTheDocument());
  return dialogContent() as HTMLElement;
}

async function createTag(user: User, name: string) {
  const dialog = dialogContent() as HTMLElement;
  await user.type(within(dialog).getByLabelText('Name'), name);
  const submit = within(dialog).getByRole('button', { name: 'Create tag' });
  expect(submit).toBeEnabled();
  await user.click(submit);
}

describe('Clients page: creating a client tag (#1119)', () => {
  /**
   * The filter popover closes itself before the dialog opens, then reopens on success with the new tag listed
   * but unticked (a fresh tag has no clients yet, so ticking it would filter the table to empty).
   */
  it('creates a tag from the filter popup and reopens it listed, unticked', async () => {
    serveClientsList(newClientsListState());
    const { user } = renderClientsPage();
    await screen.findByRole('link', { name: 'QA Client' });

    await openPopover(screen.getByRole('button', { name: 'Select tags' }), user);
    expect(within(popoverContent() as HTMLElement).getByText("You haven't created any tags yet.")).toBeVisible();

    const dialog = await openCreateDialog(user);
    expect(within(dialog).getByRole('heading', { name: 'Create a new tag' })).toBeVisible();

    const tagName = 'QA Filter Tag';
    await createTag(user, tagName);

    // Dialog closes and the filter popover reopens on its own; the same trigger click is not repeated.
    await waitFor(() => expect(dialogContent()).not.toBeInTheDocument());
    await waitFor(() => expect(popoverContent()).toBeInTheDocument());

    const newTagRow = await waitFor(() => {
      const row = within(popoverContent() as HTMLElement).getByText(tagName).closest('li');
      expect(row).toBeInTheDocument();
      return row as HTMLElement;
    });
    expect(within(newTagRow).getByRole('checkbox')).not.toBeChecked();
  });

  /** A duplicate name shows the specific translated error, not the generic fallback, and leaves the dialog open. */
  it('duplicate tag name shows the specific error toast and keeps the dialog open', async () => {
    serveClientsList(newClientsListState());
    const { user } = renderClientsPage();
    await screen.findByRole('link', { name: 'QA Client' });
    const tagName = 'QA Duplicate Tag';

    await openPopover(screen.getByRole('button', { name: 'Select tags' }), user);
    await openCreateDialog(user);
    await createTag(user, tagName);
    await waitFor(() => expect(dialogContent()).not.toBeInTheDocument());
    await waitFor(() => expect(within(popoverContent() as HTMLElement).getByText(tagName)).toBeVisible());

    // Same name again: the backend 409s.
    await openCreateDialog(user);
    await createTag(user, tagName);

    await waitFor(() => {
      const toasts = [...document.querySelectorAll("[data-slot='toast']")];
      expect(toasts.some((toast) => toast.textContent?.includes('A tag with this name already exists.'))).toBe(true);
    });
    expect(screen.queryByText("Couldn't create the tag. Please try again.")).not.toBeInTheDocument();

    // Dialog stays open on error, per the create-mutation's onError contract.
    expect(dialogContent()).toBeInTheDocument();
  });

  /** The per-row tag picker returns focus to its own trigger, not `<body>`, after Cancel and after a successful create. */
  it('the per-row tag picker returns focus to its own trigger after cancel and after create', async () => {
    serveClientsList(newClientsListState());
    const { user } = renderClientsPage();
    await screen.findByRole('link', { name: 'QA Client' });
    const rowTrigger = screen.getAllByRole('button', { name: 'Assign tags' })[0];

    // Cancel path.
    await openPopover(rowTrigger, user);
    const dialog = await openCreateDialog(user);
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(dialogContent()).not.toBeInTheDocument());
    await waitFor(() => expect(rowTrigger).toHaveFocus());

    // Create path.
    await openPopover(rowTrigger, user);
    await openCreateDialog(user);
    await createTag(user, 'QA Row Tag');
    await waitFor(() => expect(dialogContent()).not.toBeInTheDocument());
    await waitFor(() => expect(rowTrigger).toHaveFocus());
  });
});
