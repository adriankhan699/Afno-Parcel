import { useSyncExternalStore } from 'react';
import { isCounterStaff, isCustomer, isStaff } from '../lib/auth';
import { getSessionSnapshot, subscribeSession } from '../lib/session';
import type { CurrentUserResponse } from '../types/api';

export interface AuthState {
  status: 'loading' | 'authenticated' | 'anonymous';
  user: CurrentUserResponse | null;
  isAuthenticated: boolean;
  isCustomer: boolean;
  isCounterStaff: boolean;
  /** Can see every parcel, not just their own. */
  isStaff: boolean;
}

export function useAuth(): AuthState {
  const { status, user } = useSyncExternalStore(subscribeSession, getSessionSnapshot, getSessionSnapshot);
  const roles = user?.roles;
  return {
    status,
    user,
    isAuthenticated: status === 'authenticated',
    isCustomer: isCustomer(roles),
    isCounterStaff: isCounterStaff(roles),
    isStaff: isStaff(roles),
  };
}
