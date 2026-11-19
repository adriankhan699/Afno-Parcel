import { describe, expect, it } from 'vitest';
import { notificationApi } from './notificationApi';

describe('notification paths', () => {
  it('all carry the /api/v1 prefix', () => {
    const paths = [
      notificationApi.messages,
      notificationApi.message('a'),
      notificationApi.requeue('a'),
      notificationApi.cancel('a'),
      notificationApi.summary,
      notificationApi.providers,
      notificationApi.templates,
      notificationApi.template('a'),
      notificationApi.templatePreview,
      notificationApi.optOuts,
      notificationApi.optOut('a'),
      notificationApi.mockMessages,
      notificationApi.mockStatus('a'),
      notificationApi.forParcel('a'),
      notificationApi.preferences,
      notificationApi.parcelConsent('a'),
    ];
    for (const path of paths) expect(path.startsWith('/api/v1/')).toBe(true);
  });

  it('match the routes of the server', () => {
    expect(notificationApi.requeue('1')).toBe('/api/v1/notifications/messages/1/requeue');
    expect(notificationApi.cancel('1')).toBe('/api/v1/notifications/messages/1/cancel');
    expect(notificationApi.templatePreview).toBe('/api/v1/notifications/templates/preview');
    expect(notificationApi.mockStatus('wamid.1')).toBe('/api/v1/notifications/mock/messages/wamid.1/status');
    expect(notificationApi.forParcel('p1')).toBe('/api/v1/parcels/p1/notifications');
    expect(notificationApi.parcelConsent('p1')).toBe('/api/v1/notifications/consent/parcel/p1');
    expect(notificationApi.preferences).toBe('/api/v1/notifications/preferences');
  });

  it('encode values that are not safe in a path', () => {
    expect(notificationApi.mockStatus('a/b c?d')).toBe('/api/v1/notifications/mock/messages/a%2Fb%20c%3Fd/status');
    expect(notificationApi.message('../x')).toBe('/api/v1/notifications/messages/..%2Fx');
  });
});
