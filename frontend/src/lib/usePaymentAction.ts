import { ApiError } from './apiClient';
import { paymentErrorMessage } from './payment';
import { useGuardedAction, type GuardedAction } from './useGuardedAction';

/** A server error as a sentence for a payment screen: the server's own explanation where there is one. */
export function toPaymentError(err: unknown): string {
  return err instanceof ApiError ? paymentErrorMessage(err.status, err.problem.detail) : paymentErrorMessage(undefined, undefined);
}

/** One action at a time (a second tap is ignored), with payment-specific error wording. */
export function usePaymentAction(): GuardedAction {
  return useGuardedAction(toPaymentError);
}
