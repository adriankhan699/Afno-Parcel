import type { ParcelSearchQuery } from '../types/api';

export const PARCEL_PAGE_SIZE = 20;

/** The list filters exactly as the user typed them (all strings so inputs stay controlled). */
export interface ParcelFilters {
  search: string;
  status: string;
  serviceType: string;
  paymentMethod: string;
  /** '' = any, 'yes' = only COD parcels, 'no' = only non-COD parcels. */
  cod: '' | 'yes' | 'no';
  channel: string;
  bookedFrom: string;
  bookedTo: string;
}

export const emptyFilters = (): ParcelFilters => ({
  search: '',
  status: '',
  serviceType: '',
  paymentMethod: '',
  cod: '',
  channel: '',
  bookedFrom: '',
  bookedTo: '',
});

const DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;

/** Turns the filter form into the API query. Blank values are omitted so the server applies no filter. */
export function filtersToQuery(filters: ParcelFilters, page: number, pageSize = PARCEL_PAGE_SIZE): ParcelSearchQuery {
  const query: ParcelSearchQuery = { page, pageSize };
  const search = filters.search.trim();
  if (search) query.search = search;
  if (filters.status) query.status = filters.status;
  if (filters.serviceType) query.serviceType = filters.serviceType;
  if (filters.paymentMethod) query.paymentMethod = filters.paymentMethod;
  if (filters.channel) query.channel = filters.channel;
  if (filters.cod === 'yes') query.isCod = true;
  if (filters.cod === 'no') query.isCod = false;
  if (DATE_PATTERN.test(filters.bookedFrom)) query.bookedFrom = filters.bookedFrom;
  if (DATE_PATTERN.test(filters.bookedTo)) query.bookedTo = filters.bookedTo;
  return query;
}

/** "?page=1&pageSize=20&search=..." — values are percent-encoded. */
export function toQueryString(query: ParcelSearchQuery): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === '') continue;
    params.set(key, String(value));
  }
  const text = params.toString();
  return text ? `?${text}` : '';
}

/** True when the date range is back to front (the server would return nothing). */
export function isDateRangeInvalid(filters: Pick<ParcelFilters, 'bookedFrom' | 'bookedTo'>): boolean {
  return Boolean(filters.bookedFrom && filters.bookedTo && filters.bookedFrom > filters.bookedTo);
}
