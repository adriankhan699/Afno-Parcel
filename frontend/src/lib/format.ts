import type { AddressDto } from '../types/api';

// Nepal uses the South Asian (lakh/crore) digit grouping: 1,23,456.00
const nprFormatter = new Intl.NumberFormat('en-IN', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

/** Formats an amount in Nepalese Rupees, e.g. "Rs. 1,23,456.50". */
export function formatNpr(amount: number): string {
  if (!Number.isFinite(amount)) {
    throw new RangeError('Amount must be a finite number');
  }
  return `Rs. ${nprFormatter.format(amount)}`;
}

/** All timestamps from the API are UTC. Nepal has a single fixed zone (UTC+05:45, no daylight saving). */
export const NEPAL_TIME_ZONE = 'Asia/Kathmandu';

const nepalDateTimeFormatter = new Intl.DateTimeFormat('en-GB', {
  timeZone: NEPAL_TIME_ZONE,
  year: 'numeric',
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
});

/** Formats a UTC ISO timestamp from the API for display in Nepal time, e.g. "17 Nov 2026, 05:45 NPT". */
export function formatNepalDateTime(utcIso: string): string {
  const date = new Date(utcIso);
  if (Number.isNaN(date.getTime())) {
    throw new RangeError(`Invalid date: ${utcIso}`);
  }
  return `${nepalDateTimeFormatter.format(date)} NPT`;
}

/** Formats an address in the order used on Nepali parcels. */
export function formatAddress(address: AddressDto): string {
  return [
    address.streetOrTole,
    `${address.municipality}-${address.wardNumber}`,
    address.district,
    `${address.province} Province`,
    address.postalCode,
  ]
    .filter((part): part is string => Boolean(part && part.trim()))
    .join(', ');
}
