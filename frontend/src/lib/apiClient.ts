import { env } from '../config/env';
import type { ProblemDetails } from '../types/api';

/** Error thrown for any non-2xx API response, carrying the server's ProblemDetails. */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }
}

/**
 * Hooks installed by the session module so the API client can attach the bearer token and recover from an
 * expired access token without importing the session (which would be a circular dependency).
 */
interface AuthHooks {
  getAccessToken: () => string | null;
  /** Obtains a new access token (rotating the refresh token). Returns null when the session cannot be renewed. */
  refreshAccessToken: () => Promise<string | null>;
}

let authHooks: AuthHooks | null = null;

export function setAuthHooks(hooks: AuthHooks | null): void {
  authHooks = hooks;
}

export interface ApiRequestInit extends RequestInit {
  /** Do not attach the bearer token and do not try to refresh on 401 (used by the auth endpoints themselves). */
  skipAuth?: boolean;
}

async function parseProblem(response: Response): Promise<ProblemDetails> {
  const contentType = response.headers.get('content-type') ?? '';
  if (contentType.includes('json')) {
    try {
      return (await response.json()) as ProblemDetails;
    } catch {
      // fall through to a generic problem
    }
  }
  return { status: response.status, title: response.statusText || 'Request failed' };
}

function send(path: string, init: ApiRequestInit, token: string | null): Promise<Response> {
  const { skipAuth: _skipAuth, ...fetchInit } = init;
  void _skipAuth;
  const headers = new Headers(fetchInit.headers);
  headers.set('Accept', 'application/json');
  if (fetchInit.body !== undefined && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  if (token) {
    headers.set('Authorization', `Bearer ${token}`);
  }
  return fetch(`${env.apiBaseUrl}${path}`, { ...fetchInit, headers });
}

export async function apiRequest<T>(path: string, init: ApiRequestInit = {}): Promise<T> {
  const useAuth = !init.skipAuth && authHooks !== null;
  let response = await send(path, init, useAuth ? authHooks!.getAccessToken() : null);

  if (response.status === 401 && useAuth) {
    // The access token is short-lived: renew it once and retry. If that fails the caller sees the 401.
    const renewed = await authHooks!.refreshAccessToken();
    if (renewed) {
      response = await send(path, init, renewed);
    }
  }

  if (!response.ok) {
    throw new ApiError(response.status, await parseProblem(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

export const api = {
  get: <T>(path: string, signal?: AbortSignal) => apiRequest<T>(path, { method: 'GET', signal }),
  post: <T>(path: string, body: unknown, signal?: AbortSignal, init: ApiRequestInit = {}) =>
    apiRequest<T>(path, { ...init, method: 'POST', body: JSON.stringify(body), signal }),
};
