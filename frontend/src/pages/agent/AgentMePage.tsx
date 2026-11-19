import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useAuth } from '../../auth/useAuth';
import { AvailabilityToggle, BigButton, InfoRow, Panel } from '../../components/agent/AgentBits';
import { Alert } from '../../components/form';
import { api } from '../../lib/apiClient';
import { deliveryApi } from '../../lib/deliveryApi';
import { formatNpr } from '../../lib/format';
import { logout } from '../../lib/session';
import { useGuardedAction } from '../../lib/useGuardedAction';
import { useStableApi } from '../../lib/useStableApi';
import type { AgentSummaryResponse } from '../../types/api';

/** The agent's profile, their availability and a way to sign out. */
export default function AgentMePage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const [reloadKey, setReloadKey] = useState(0);
  const availability = useGuardedAction();
  const summary = useStableApi<AgentSummaryResponse>(deliveryApi.me, reloadKey);

  async function setAvailable(isAvailable: boolean) {
    await availability.run(() => api.put(deliveryApi.myAvailability, { isAvailable }));
    setReloadKey((k) => k + 1);
  }

  async function handleSignOut() {
    await logout();
    navigate('/');
  }

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold tracking-tight">Me</h1>
      {summary.error && <Alert>{summary.error}</Alert>}
      {summary.loading && <p className="text-base text-slate-500">Loading…</p>}

      <Panel>
        <dl className="divide-y divide-slate-100">
          <InfoRow label="Name">{summary.data?.fullName ?? auth.user?.fullName}</InfoRow>
          <InfoRow label="Email">{auth.user?.email}</InfoRow>
          {summary.data && <InfoRow label="Branch">{summary.data.branchName}</InfoRow>}
          {summary.data && <InfoRow label="To deliver">{summary.data.openAssignments}</InfoRow>}
          {summary.data && <InfoRow label="Cash in hand">{formatNpr(summary.data.codCollectedUnsettled)}</InfoRow>}
        </dl>
      </Panel>

      {availability.error && <Alert>{availability.error}</Alert>}
      {summary.data && <AvailabilityToggle isAvailable={summary.data.isAvailable} busy={availability.busy} onChange={(next) => void setAvailable(next)} />}

      <p className="text-sm text-slate-500">This app does not track your location. Only the status of each delivery is recorded.</p>

      <BigButton variant="secondary" onClick={() => void handleSignOut()}>
        Sign out
      </BigButton>
    </div>
  );
}
