/**
 * Paths of the notification endpoints (SMS and WhatsApp) in one place, so a route change on the server is a one-line fix here.
 * All paths are relative to the API origin and already carry the /api/v1 prefix.
 */
const enc = encodeURIComponent;

export const notificationApi = {
  // Staff: the messages (SuperAdmin full access; BranchManager read-only)
  messages: '/api/v1/notifications/messages',
  message: (id: string) => `/api/v1/notifications/messages/${enc(id)}`,
  /** POST {reason?} (SuperAdmin): try a failed or given-up message again now. */
  requeue: (id: string) => `/api/v1/notifications/messages/${enc(id)}/requeue`,
  /** POST {reason?} (SuperAdmin): stop a queued or failed message from being sent. */
  cancel: (id: string) => `/api/v1/notifications/messages/${enc(id)}/cancel`,
  summary: '/api/v1/notifications/summary',
  providers: '/api/v1/notifications/providers',

  // Templates (PUT is SuperAdmin; preview is POST)
  templates: '/api/v1/notifications/templates',
  template: (id: string) => `/api/v1/notifications/templates/${enc(id)}`,
  templatePreview: '/api/v1/notifications/templates/preview',

  // Opt-outs (POST and DELETE are SuperAdmin)
  optOuts: '/api/v1/notifications/opt-outs',
  optOut: (id: string) => `/api/v1/notifications/opt-outs/${enc(id)}`,

  // The mock provider's test inbox (SuperAdmin; 404 when no mock provider is active)
  mockMessages: '/api/v1/notifications/mock/messages',
  mockStatus: (providerMessageId: string) => `/api/v1/notifications/mock/messages/${enc(providerMessageId)}/status`,

  // A parcel's messages (staff who can read parcels)
  forParcel: (parcelId: string) => `/api/v1/parcels/${enc(parcelId)}/notifications`,

  // Consent
  /** The signed-in customer's own choices (GET and PUT). */
  preferences: '/api/v1/notifications/preferences',
  /** The consent of a parcel's SENDER, recorded at the counter (GET and PUT; counter staff). */
  parcelConsent: (parcelId: string) => `/api/v1/notifications/consent/parcel/${enc(parcelId)}`,
} as const;
