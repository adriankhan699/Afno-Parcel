import type { AuthResponse, CurrentUserResponse, LoginRequest, RegisterRequest } from '../types/api';
import { ApiError, api, setAuthHooks } from './apiClient';
import { clearRefreshToken, readRefreshToken, writeRefreshToken } from './auth';

/**
 * The signed-in session, kept in a tiny external store so React reads it with useSyncExternalStore.
 * The access token lives only in this module's memory; the refresh token is in sessionStorage (see auth.ts).
 */
export type SessionStatus = 'loading' | 'authenticated' | 'anonymous';

export interface SessionSnapshot {
  status: SessionStatus;
  user: CurrentUserResponse | null;
}

let accessToken: string | null = null;
let snapshot: SessionSnapshot = {
  // Until we know whether the stored refresh token still works we are "loading", not "signed out".
  status: readRefreshToken() ? 'loading' : 'anonymous',
  user: null,
};
const listeners = new Set<() => void>();

function publish(next: SessionSnapshot): void {
  snapshot = next;
  listeners.forEach((listener) => listener());
}

export const subscribeSession = (listener: () => void): (() => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};

export const getSessionSnapshot = (): SessionSnapshot => snapshot;

function applyAuth(auth: AuthResponse): void {
  accessToken = auth.accessToken;
  writeRefreshToken(auth.refreshToken);
  publish({ status: 'authenticated', user: auth.user });
}

function endSession(): void {
  accessToken = null;
  clearRefreshToken();
  publish({ status: 'anonymous', user: null });
}

let refreshInFlight: Promise<string | null> | null = null;

/**
 * Exchanges the stored refresh token for a new pair. Refresh tokens are single-use, so concurrent callers
 * (React StrictMode, several requests failing at once) must share one request.
 */
export function refreshSession(): Promise<string | null> {
  if (refreshInFlight) return refreshInFlight;

  const refreshToken = readRefreshToken();
  if (!refreshToken) {
    if (snapshot.status !== 'anonymous') endSession();
    return Promise.resolve(null);
  }

  refreshInFlight = api
    .post<AuthResponse>('/api/v1/auth/refresh', { refreshToken }, undefined, { skipAuth: true })
    .then((auth) => {
      applyAuth(auth);
      return auth.accessToken;
    })
    .catch((err: unknown) => {
      // A rejected token means the session is over; a network error keeps it so a retry can succeed later.
      if (err instanceof ApiError && (err.status === 401 || err.status === 400)) {
        endSession();
      } else if (snapshot.status === 'loading') {
        publish({ status: 'anonymous', user: null });
      }
      return null;
    })
    .finally(() => {
      refreshInFlight = null;
    });

  return refreshInFlight;
}

setAuthHooks({
  getAccessToken: () => accessToken,
  refreshAccessToken: refreshSession,
});

/** Restores the session after a page load. Safe to call more than once. */
export async function restoreSession(): Promise<void> {
  if (snapshot.status === 'loading') {
    await refreshSession();
  }
}

export async function login(request: LoginRequest): Promise<CurrentUserResponse> {
  const auth = await api.post<AuthResponse>('/api/v1/auth/login', request, undefined, { skipAuth: true });
  applyAuth(auth);
  return auth.user;
}

export async function register(request: RegisterRequest): Promise<CurrentUserResponse> {
  const auth = await api.post<AuthResponse>('/api/v1/auth/register', request, undefined, { skipAuth: true });
  applyAuth(auth);
  return auth.user;
}

export async function logout(): Promise<void> {
  const refreshToken = readRefreshToken();
  endSession();
  if (refreshToken) {
    try {
      await api.post<void>('/api/v1/auth/logout', { refreshToken }, undefined, { skipAuth: true });
    } catch {
      // The local session is already gone; the server-side token will expire on its own.
    }
  }
}
