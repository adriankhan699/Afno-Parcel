import ConsentEditor from '../components/ConsentEditor';
import { api } from '../lib/apiClient';
import { notificationApi } from '../lib/notificationApi';
import type { NotificationPreferencesResponse, UpdateNotificationPreferencesRequest } from '../types/api';

const savePreferences = (body: UpdateNotificationPreferencesRequest) => api.put<NotificationPreferencesResponse>(notificationApi.preferences, body);

/** A signed-in customer chooses whether they get updates about their parcels by SMS and by WhatsApp. Both are off until they say yes. */
export default function AccountNotificationsPage() {
  return (
    <div className="mx-auto max-w-2xl space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">Parcel updates</h1>
        <p className="mt-2 text-sm text-slate-600">
          Choose how we tell you about your parcels. We only send updates about your parcels (booked, on its way, out for delivery, delivered), never advertising.
        </p>
      </div>
      <section aria-labelledby="choices-heading" className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
        <h2 id="choices-heading" className="text-base font-semibold text-slate-900">
          Your choices
        </h2>
        <div className="mt-4">
          <ConsentEditor
            loadPath={notificationApi.preferences}
            save={savePreferences}
            idPrefix="account"
            smsLabel="SMS updates"
            whatsAppLabel="WhatsApp updates"
            smsDescription="Text messages about your parcels, sent to the phone number on your account."
            whatsAppDescription="WhatsApp messages about your parcels, sent to the phone number on your account."
            savedMessage="Your choices are saved."
          />
        </div>
      </section>
      <ul className="list-disc space-y-1 pl-5 text-sm text-slate-600">
        <li>Both are off until you switch them on. You can change your mind any time on this page.</li>
        <li>On WhatsApp you can also reply STOP to any message: that stops our WhatsApp messages to your number straight away.</li>
      </ul>
    </div>
  );
}
