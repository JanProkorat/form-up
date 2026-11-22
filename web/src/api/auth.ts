import type {
  LoginResponse,
  RegisterRequest,
  RegisterResponse,
  RequestPasswordResetRequest,
  ResetPasswordRequest,
} from '@/api/client';
import type {
  AnonymousResendVerificationResponse,
  ResolveInviteTokenRequest,
  ResolveInviteTokenResponse,
  VerifyEmailRequest,
  VerifyEmailResponse,
} from '@/api/generated';
import api from '@/lib/api';

/**
 * POST /auth/login
 * Authenticates with an email/password pair and returns platform JWT tokens.
 *
 * Deliberately built on `api.post` (not `apiClient.loginEndpoint` from the
 * generated NSwag client) so a failure surfaces as an `AxiosError` — the
 * generated client throws `ApiException` instead, which `lib/api-errors.ts`'s
 * `getErrorCode()`/`getApiErrorMessage()` cannot read, collapsing every
 * login failure to one generic message. See the sibling `googleSocialLogin`/
 * `appleSocialLogin` helpers, which use the same pattern.
 *
 * Invalid credentials and a deactivated account both come back as HTTP 400
 * (not 401), with the machine-readable code at `errors[0].code` (`reason`
 * carries the human-readable message — see `lib/api-errors.ts`).
 */
export async function login(email: string, password: string): Promise<LoginResponse> {
  const { data } = await api.post<LoginResponse>('/auth/login', { email, password });
  return data;
}

/**
 * POST /auth/social/nonce
 * Requests a single-use, server-issued nonce from the backend.
 * The raw nonce must be passed to the IdP sign-in (Apple/Google) and then
 * sent back in the login body — the backend verifies it was not replayed.
 * TTL is 10 minutes; the nonce is consumed on first use.
 *
 * @returns The raw nonce string.
 */
export async function requestSocialNonce(): Promise<string> {
  const { data } = await api.post<{ nonce: string }>('/auth/social/nonce');
  return data.nonce;
}

/**
 * POST /auth/social/google
 * Sends a Google ID token JWT and the raw nonce to the backend, which verifies
 * the ID token via GoogleJsonWebSignature.ValidateAsync (checking the nonce
 * claim matches) and returns platform JWT tokens.
 *
 * @param idToken - The credential JWT from @react-oauth/google's GoogleLogin
 *                  onSuccess callback (credentialResponse.credential).
 *                  This is an ID token, NOT an OAuth access token.
 *                  Note: do NOT pass an OAuth access token — the backend will reject it.
 * @param nonce   - The raw nonce previously obtained via requestSocialNonce().
 *                  Google embeds the raw nonce into the id_token nonce claim;
 *                  the backend compares it directly.
 */
export async function googleSocialLogin(idToken: string, nonce: string): Promise<LoginResponse> {
  const { data } = await api.post<LoginResponse>('/auth/social/google', { idToken, nonce });
  return data;
}

/**
 * POST /auth/social/apple
 * Sends Apple identity token (and optional first-auth fields) plus the raw nonce
 * to the backend, which verifies the JWT against Apple's JWKS and returns
 * platform JWT tokens. Apple embeds SHA-256(rawNonce) in the id_token nonce
 * claim; the web client passes the raw nonce and the backend hashes it to compare.
 *
 * @param payload.identityToken  - Apple identity token JWT from signInWithApple().
 * @param payload.authorizationCode - Authorization code from Apple (forwarded
 *   for forward-compat; backend ignores it today — no .p8 exchange is done).
 * @param payload.firstName - Present only on first Apple authorization; absent
 *   on re-auth. Backend persists it on new account provision only.
 * @param payload.lastName  - Same as firstName.
 * @param payload.nonce     - The raw nonce previously obtained via requestSocialNonce().
 */
export async function appleSocialLogin(payload: {
  identityToken: string;
  authorizationCode?: string;
  firstName?: string;
  lastName?: string;
  nonce: string;
}): Promise<LoginResponse> {
  const { data } = await api.post<LoginResponse>('/auth/social/apple', payload);
  return data;
}

/**
 * POST /auth/register
 * Creates a new user account. Success is 201, not 200 (`RegisterEndpoint`
 * uses `Send.ResponseAsync(..., StatusCodes.Status201Created, ct)`).
 *
 * Built on `api.post` (not the generated NSwag client) for the same reason
 * as `login` above — a rejected request must surface as an `AxiosError` so
 * `lib/api-errors.ts` can read it. Duplicate-email has no machine-readable
 * error code: `RegisterEndpoint` pipes ASP.NET Identity's `IdentityResult`
 * errors through as a bare `ThrowIfAnyErrors()`, so the 400 body carries an
 * untranslated English `errors[].reason` (e.g. "Email 'x' is already
 * taken.") and an empty `code` — callers must match `reason` heuristically
 * rather than looking up an `apiErrors.*` translation key.
 */
export async function register(payload: RegisterRequest): Promise<RegisterResponse> {
  const { data } = await api.post<RegisterResponse>('/auth/register', payload);
  return data;
}

/**
 * POST /auth/invite/resolve
 * Resolves an invite-link token to the invited email. Any unknown, expired or
 * used token is a bare 404 (400 for a malformed one, 429 when rate limited);
 * callers must treat every failure the same and never reveal why it failed.
 */
export async function resolveInviteToken(token: string): Promise<ResolveInviteTokenResponse> {
  const payload: ResolveInviteTokenRequest = { token };
  const { data } = await api.post<ResolveInviteTokenResponse>('/auth/invite/resolve', payload);
  return data;
}

/**
 * POST /auth/resend-verification/anonymous
 * Resends the verification email, keyed by email address instead of an
 * authenticated session. Always returns the same generic 200 body for an
 * unregistered email, an already-verified account, a throttled sender
 * (3 per rolling 24h), and a genuine send — never surface a differing
 * message or a remaining-sends count, or the response becomes an
 * account-existence oracle.
 */
export async function resendVerificationAnonymous(
  email: string
): Promise<AnonymousResendVerificationResponse> {
  const { data } = await api.post<AnonymousResendVerificationResponse>(
    '/auth/resend-verification/anonymous',
    { email }
  );
  return data;
}

/**
 * POST /auth/verify-email
 * Verifies a user's email address using the token from the verification
 * link. Consumes the token — a second call with the same token returns
 * INVALID_VERIFICATION_TOKEN even though the first call succeeded (see
 * VerifyEmailPage's StrictMode double-invoke guard).
 *
 * Built on `api.post` for the same AxiosError reason as the other helpers
 * in this file — the generated `verifyEmailEndpoint` throws `ApiException`,
 * which `lib/api-errors.ts` cannot read.
 */
export async function verifyEmail(token: string): Promise<VerifyEmailResponse> {
  const payload: VerifyEmailRequest = { token };
  const { data } = await api.post<VerifyEmailResponse>('/auth/verify-email', payload);
  return data;
}

/**
 * POST /auth/password/reset
 * Requests a password reset link. Returns 200 when sent; 404 with errorCode
 * EMAIL_NOT_REGISTERED for an unknown email; 429 when rate limited.
 *
 * Note the verb collision with `resetPassword` below: both endpoints live
 * at the same path, distinguished only by HTTP verb (`RequestPasswordResetEndpoint`
 * vs `ResetPasswordEndpoint`).
 */
export async function requestPasswordReset(email: string): Promise<void> {
  const payload: RequestPasswordResetRequest = { email };
  await api.post('/auth/password/reset', payload);
}

/**
 * PUT /auth/password/reset
 * Completes a password reset using the token + email from the reset link.
 * Returns one generic failure for an invalid/expired/already-used token AND
 * for an unknown email (anti-enumeration, #656) — do not try to distinguish
 * them client-side. A weak password is a 400 with a `newPassword` field
 * error. Success revokes all of the user's refresh tokens; it does not sign
 * the caller in.
 */
export async function resetPassword(payload: ResetPasswordRequest): Promise<void> {
  await api.put('/auth/password/reset', payload);
}
