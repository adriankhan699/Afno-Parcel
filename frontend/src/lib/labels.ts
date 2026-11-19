import type { PaymentMethod, ServiceType } from '../types/api';

export const SERVICE_LABELS: Record<ServiceType, string> = {
  Standard: 'Standard',
  Express: 'Express',
  SameDay: 'Same day',
};

export const SERVICE_HINTS: Record<ServiceType, string> = {
  Standard: 'Lowest price, delivered in the usual time for the district.',
  Express: 'Roughly half the usual delivery time.',
  SameDay: 'Delivered within hours. Only available on some routes.',
};

export const PAYMENT_LABELS: Record<PaymentMethod, string> = {
  Cash: 'Cash at the branch',
  Esewa: 'eSewa',
  Khalti: 'Khalti',
  ConnectIps: 'ConnectIPS',
  BankTransfer: 'Bank transfer',
  CashOnDelivery: 'Cash on delivery (receiver pays)',
};

export const STATUS_LABELS: Record<string, string> = {
  BookingCreated: 'Booking created',
  PickedUp: 'Picked up',
  Shipped: 'Shipped',
  InTransit: 'In transit',
  ArrivedAtWarehouse: 'Arrived at warehouse',
  OutForDelivery: 'Out for delivery',
  Delivered: 'Delivered',
  DeliveryFailed: 'Delivery attempt failed',
  Returned: 'Returned',
  Cancelled: 'Cancelled',
};

export const CHANNEL_LABELS: Record<string, string> = { Online: 'Online', Counter: 'Counter' };

export function serviceLabel(value: string): string {
  return SERVICE_LABELS[value as ServiceType] ?? value;
}

export function paymentLabel(value: string): string {
  return PAYMENT_LABELS[value as PaymentMethod] ?? value;
}

export function statusLabel(value: string): string {
  return STATUS_LABELS[value] ?? value;
}

/** Tailwind classes for a status pill. */
export function statusTone(status: string): string {
  switch (status) {
    case 'Delivered':
      return 'bg-emerald-50 text-emerald-700';
    case 'Cancelled':
    case 'DeliveryFailed':
    case 'Returned':
      return 'bg-red-50 text-red-700';
    case 'BookingCreated':
      return 'bg-slate-100 text-slate-700';
    default:
      return 'bg-blue-50 text-blue-700';
  }
}

/** "2.5 kg" — trims trailing zeros. */
export function formatWeight(kg: number): string {
  return `${Number(kg.toFixed(3))} kg`;
}
