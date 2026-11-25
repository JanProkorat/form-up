import api from '@/lib/api';

/**
 * Sends a bodyless-by-design POST/PUT with an explicit `{}` JSON body.
 *
 * FastEndpoints answers 415 to a request without a JSON content type, and the generated
 * client sends none for endpoints with no request DTO (copy / hide / unhide). Goes through
 * the parsing axios instance so a failure is an `AxiosError` that `getErrorCode` reads.
 */
export async function postEmptyJson(url: string): Promise<void> {
  await api.post(url, {});
}

export async function putEmptyJson(url: string): Promise<void> {
  await api.put(url, {});
}
