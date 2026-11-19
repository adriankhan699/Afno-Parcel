import { ApiError } from './apiClient';
import { notificationErrorMessage } from './notification';
import { useGuardedAction, type GuardedAction } from './useGuardedAction';

/** A server error as a sentence for a notifications screen: the server's own explanation where there is one. */
export function toNotificationError(err: unknown): string {
  return err instanceof ApiError ? notificationErrorMessage(err.status, err.problem.detail) : notificationErrorMessage(undefined, undefined);
}

/** One action at a time (a second tap is ignored), with notification-specific error wording. */
export function useNotificationAction(): GuardedAction {
  return useGuardedAction(toNotificationError);
}
