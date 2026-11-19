import { Link, useSearchParams } from 'react-router';
import { useAuth } from '../../auth/useAuth';
import { Alert } from '../../components/form';
import { canEditNotifications, canUseMockInbox } from '../../lib/auth';
import { TAB_LABELS, mockInboxAvailable, mockMessagesPath, parseTab, visibleTabs } from '../../lib/notification';
import { useApi } from '../../lib/useApi';
import type { MockProviderMessageResponse, PagedResponse } from '../../types/api';
import MessagesTab from './notifications/MessagesTab';
import OptOutsTab from './notifications/OptOutsTab';
import OverviewTab from './notifications/OverviewTab';
import TemplatesTab from './notifications/TemplatesTab';
import TestInboxTab from './notifications/TestInboxTab';

/**
 * SMS and WhatsApp notifications: the providers and numbers, every message and where it got to, the texts, the opt-outs and,
 * for a super admin while a mock provider is active, the test inbox. A branch manager can read everything and change nothing.
 */
export default function NotificationsAdminPage() {
  const auth = useAuth();
  const roles = auth.user?.roles;
  const canEdit = canEditNotifications(roles);
  const mockAllowed = canUseMockInbox(roles);

  // The test inbox exists only while a mock provider is active (the server answers 404 otherwise), so ask before offering the tab.
  const probe = useApi<PagedResponse<MockProviderMessageResponse>>(mockAllowed ? mockMessagesPath(1, 1) : null);
  const tabs = visibleTabs({ canUseMockInbox: mockAllowed, mockInboxAvailable: mockInboxAvailable(probe) });

  const [params] = useSearchParams();
  const tab = parseTab(params.get('tab'), tabs);

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold">Notifications</h2>
        <p className="mt-1 text-sm text-slate-600">SMS and WhatsApp messages about parcels: what was sent, and what the providers reported back.</p>
      </div>

      {!canEdit && <Alert tone="info">You can read everything here. Only a super admin can retry or cancel messages, change templates and opt-outs.</Alert>}

      <nav aria-label="Notifications" className="flex flex-wrap gap-1 border-b border-slate-200">
        {tabs.map((t) => (
          <Link
            key={t}
            to={{ search: `?tab=${t}` }}
            aria-current={t === tab ? 'page' : undefined}
            className={`whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
              t === tab ? 'border-brand-600 text-brand-700' : 'border-transparent text-slate-600 hover:border-slate-300 hover:text-slate-900'
            }`}
          >
            {TAB_LABELS[t]}
          </Link>
        ))}
      </nav>

      {tab === 'overview' && <OverviewTab />}
      {tab === 'messages' && <MessagesTab canEdit={canEdit} />}
      {tab === 'templates' && <TemplatesTab canEdit={canEdit} />}
      {tab === 'opt-outs' && <OptOutsTab canEdit={canEdit} />}
      {tab === 'test-inbox' && mockAllowed && <TestInboxTab />}
    </div>
  );
}
