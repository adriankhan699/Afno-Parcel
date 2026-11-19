import { useEffect, type ReactNode } from 'react';
import { restoreSession } from '../lib/session';

/** Restores the session from the stored refresh token once, when the app starts. */
export default function AuthProvider({ children }: { children: ReactNode }) {
  useEffect(() => {
    void restoreSession();
  }, []);

  return <>{children}</>;
}
