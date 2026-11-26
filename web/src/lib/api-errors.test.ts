import { describe, expect, it } from 'vitest';
import { AxiosError } from 'axios';
import type { AxiosResponse } from 'axios';
import { ApiException } from '@/api/generated';
import i18n from '@/i18n';
import { getApiErrorMessage, getErrorCode } from '@/lib/api-errors';

type Body = Record<string, unknown>;

function axiosError(data: unknown, status = 409): AxiosError {
  return new AxiosError('failed', undefined, undefined, undefined, { status, data } as AxiosResponse);
}

/** The error shapes a failed request reaches the callers in; `rawApi` hands axios errors a JSON string. */
const shapes: [string, (body: Body) => unknown][] = [
  ['AxiosError, object body', (body) => axiosError(body)],
  ['AxiosError, string body', (body) => axiosError(JSON.stringify(body))],
  ['thrown body', (body) => body],
  ['ApiException, string response', (body) => new ApiException('failed', 409, JSON.stringify(body), {}, null)],
];

const topLevel = (code: string): Body => ({ status: 409, title: 'Conflict', errorCode: code });
const validation = (code: string): Body => ({ errors: [{ name: 'generalErrors', reason: 'invalid', code }] });

describe('getApiErrorMessage', () => {
  it.each(shapes)('translates a top-level errorCode: %s', (_name, build) => {
    expect(getApiErrorMessage(build(topLevel('SESSION_ALREADY_COMPLETED')), 'common.error')).toBe(
      i18n.t('apiErrors.SESSION_ALREADY_COMPLETED'),
    );
  });

  it.each(shapes)('translates a validation errors[0].code: %s', (_name, build) => {
    expect(getApiErrorMessage(build(validation('START_DATE_NOT_MONDAY')), 'common.error')).toBe(
      i18n.t('apiErrors.START_DATE_NOT_MONDAY'),
    );
  });

  it.each([
    ['top-level', topLevel('NO_SUCH_CODE_ANYWHERE')],
    ['validation', validation('NO_SUCH_CODE_ANYWHERE')],
  ])('falls back to the fallback key for an unknown %s code', (_name, body) => {
    expect(getApiErrorMessage(axiosError(body), 'common.error')).toBe(i18n.t('common.error'));
  });
});

describe('getErrorCode', () => {
  it.each(shapes)('reads a top-level errorCode: %s', (_name, build) => {
    expect(getErrorCode(build(topLevel('EMAIL_NOT_VERIFIED')))).toBe('EMAIL_NOT_VERIFIED');
  });

  it('prefers the top-level errorCode over errors[0].code and falls back to reason', () => {
    expect(getErrorCode({ errorCode: 'TOP', ...validation('INNER') })).toBe('TOP');
    expect(getErrorCode({ errors: [{ name: '', reason: 'ONLY_REASON' }] })).toBe('ONLY_REASON');
  });

  it.each([null, undefined, 'text', new Error('boom'), axiosError('not json'), axiosError({})])(
    'returns null when there is no code: %s',
    (error) => {
      expect(getErrorCode(error)).toBeNull();
    },
  );
});
