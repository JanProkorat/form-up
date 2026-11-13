import { z } from 'zod';

export const REASON_MAX = 300;

/** Why an admin acts on a coach; stored in the coach's history. */
export const reasonSchema = z.object({
  reason: z
    .string()
    .trim()
    .min(1, 'adminCoaches.validation.reasonRequired')
    .max(REASON_MAX, 'adminCoaches.validation.reasonTooLong'),
});

export type ReasonFormValues = z.infer<typeof reasonSchema>;

/** End of the given local calendar day ("YYYY-MM-DD"), or null when the text is not a date. */
export function endOfLocalDay(value: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) {
    return null;
  }
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]), 23, 59, 59);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** Tomorrow as "YYYY-MM-DD" in local time: the earliest day a gift can end on. */
export function tomorrowInputValue(now: Date = new Date()): string {
  const next = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
  const pad = (value: number) => String(value).padStart(2, '0');
  return `${next.getFullYear()}-${pad(next.getMonth() + 1)}-${pad(next.getDate())}`;
}

export const giveFreeSchema = z
  .object({
    planCode: z.string(),
    noEnd: z.boolean(),
    freeUntil: z.string(),
    reason: reasonSchema.shape.reason,
  })
  .superRefine((values, ctx) => {
    if (!values.planCode) {
      ctx.addIssue({ code: 'custom', path: ['planCode'], message: 'adminCoaches.validation.planRequired' });
    }
    if (!values.noEnd) {
      const end = endOfLocalDay(values.freeUntil);
      if (!end) {
        ctx.addIssue({ code: 'custom', path: ['freeUntil'], message: 'adminCoaches.validation.dateRequired' });
      } else if (end.getTime() <= Date.now()) {
        ctx.addIssue({ code: 'custom', path: ['freeUntil'], message: 'adminCoaches.validation.dateFuture' });
      }
    }
  });

export type GiveFreeFormValues = z.infer<typeof giveFreeSchema>;
