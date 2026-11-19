import { useAuth } from '../auth/useAuth';
import { adminApi } from './adminApi';
import { isSuperAdmin } from './auth';
import { useApi } from './useApi';
import type { BranchResponse, MyAssignmentResponse, PagedResponse } from '../types/api';
import type { SelectOption } from '../components/form';

export interface ManagerScope {
  superAdmin: boolean;
  /** False until we know which branch a branch manager works at. */
  ready: boolean;
  /** A branch manager's own branch; null for a super admin (any branch). */
  limitToBranchId: string | null;
  /** True for a branch manager who has not been assigned to a branch yet: they cannot manage anything until they are. */
  unassignedManager: boolean;
  /** Active branches the person may use: all of them for a super admin, only their own for a branch manager. */
  branchOptions: SelectOption[];
}

/**
 * What a super admin or branch manager may reach on the delivery screens. A branch manager is limited to their own branch,
 * which the server enforces too; this only keeps the forms from offering what would be refused.
 */
export function useManagerScope(): ManagerScope {
  const auth = useAuth();
  const superAdmin = isSuperAdmin(auth.user?.roles);
  const mine = useApi<MyAssignmentResponse>(superAdmin ? null : adminApi.myAssignment);
  const branches = useApi<PagedResponse<BranchResponse>>(`${adminApi.branches}?page=1&pageSize=100`);

  const myBranchId = mine.status === 'loaded' ? (mine.data.assignment?.branchId ?? '') : null;
  const all = branches.status === 'loaded' ? branches.data.items.filter((b) => b.isActive) : [];
  const visible = superAdmin ? all : all.filter((b) => b.id === myBranchId);

  return {
    superAdmin,
    ready: superAdmin || mine.status === 'loaded' || mine.status === 'error',
    limitToBranchId: superAdmin ? null : myBranchId,
    unassignedManager: !superAdmin && mine.status === 'loaded' && !mine.data.assignment,
    branchOptions: visible.map((b) => ({ value: b.id, label: `${b.name} (${b.code})` })),
  };
}
