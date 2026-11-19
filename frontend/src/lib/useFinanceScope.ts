import { useAuth } from '../auth/useAuth';
import type { SelectOption } from '../components/form';
import { adminApi } from './adminApi';
import { canChooseFinanceBranch } from './auth';
import { useApi } from './useApi';
import type { BranchResponse, PagedResponse } from '../types/api';

export interface FinanceScope {
  /** Super admins and accountants can look at any branch; a branch manager always gets their own (the server decides). */
  canChooseBranch: boolean;
  branchOptions: SelectOption[];
}

/** Which branches the finance pages may offer in their filters. */
export function useFinanceScope(): FinanceScope {
  const auth = useAuth();
  const canChooseBranch = canChooseFinanceBranch(auth.user?.roles);
  const branches = useApi<PagedResponse<BranchResponse>>(canChooseBranch ? `${adminApi.branches}?page=1&pageSize=100` : null);
  const options =
    branches.status === 'loaded' ? branches.data.items.filter((b) => b.isActive).map((b) => ({ value: b.id, label: `${b.name} (${b.code})` })) : [];
  return { canChooseBranch, branchOptions: options };
}
