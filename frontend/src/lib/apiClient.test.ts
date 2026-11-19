import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api, apiBlob, setAuthHooks } from './apiClient';

type FetchCall = { url: string; init: RequestInit & { headers: Headers } };

let calls: FetchCall[];
let responses: Response[];

beforeEach(() => {
  calls = [];
  responses = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init: RequestInit & { headers: Headers }) => {
      calls.push({ url, init });
      return Promise.resolve(responses.shift() ?? new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } }));
    }),
  );
  setAuthHooks({ getAccessToken: () => 'token-1', refreshAccessToken: () => Promise.resolve('token-2') });
});

const firstCall = () => calls[0]!;
const secondCall = () => calls[1]!;

afterEach(() => {
  setAuthHooks(null);
  vi.unstubAllGlobals();
});

describe('postForm', () => {
  it('sends the form as it is, leaving the Content-Type (with its boundary) to the browser', async () => {
    const form = new FormData();
    form.set('type', 'Photo');
    await api.postForm('/api/v1/agent/assignments/a1/proof/file', form);

    const { init } = firstCall();
    expect(init.method).toBe('POST');
    expect(init.body).toBe(form);
    expect(init.headers.has('Content-Type')).toBe(false);
    expect(init.headers.get('Authorization')).toBe('Bearer token-1');
  });

  it('still sends JSON with its Content-Type for ordinary posts', async () => {
    await api.post('/x', { a: 1 });
    expect(firstCall().init.headers.get('Content-Type')).toBe('application/json');
  });
});

describe('apiBlob', () => {
  it('downloads with the bearer token in a header, never in the URL', async () => {
    responses.push(new Response('imagebytes', { status: 200, headers: { 'content-type': 'image/jpeg' } }));

    const blob = await apiBlob('/api/v1/delivery/proofs/p1/file');

    expect(await blob.text()).toBe('imagebytes');
    expect(firstCall().url).not.toContain('token');
    expect(firstCall().init.headers.get('Authorization')).toBe('Bearer token-1');
    expect(firstCall().init.headers.get('Accept')).toBe('*/*');
  });

  it('renews an expired token once and retries', async () => {
    responses.push(new Response('{}', { status: 401, headers: { 'content-type': 'application/problem+json' } }));
    responses.push(new Response('ok', { status: 200 }));

    await apiBlob('/file');

    expect(calls).toHaveLength(2);
    expect(secondCall().init.headers.get('Authorization')).toBe('Bearer token-2');
  });

  it('throws the server\'s problem for a refused download', async () => {
    responses.push(new Response(JSON.stringify({ status: 404, detail: 'No such proof.' }), { status: 404, headers: { 'content-type': 'application/problem+json' } }));

    await expect(apiBlob('/file')).rejects.toMatchObject({ status: 404, message: 'No such proof.' });
    responses.push(new Response('{}', { status: 403, headers: { 'content-type': 'application/json' } }));
    await expect(apiBlob('/file')).rejects.toBeInstanceOf(ApiError);
  });
});
