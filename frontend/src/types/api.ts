// Mirrors NepalParcel.Contracts. Keep in sync with the backend DTOs (or generate from Swagger later).

export const PROVINCES = [
  'Koshi',
  'Madhesh',
  'Bagmati',
  'Gandaki',
  'Lumbini',
  'Karnali',
  'Sudurpashchim',
] as const;

export type Province = (typeof PROVINCES)[number];

export interface AddressDto {
  province: Province | string;
  district: string;
  municipality: string;
  wardNumber: number;
  streetOrTole?: string | null;
  landmark?: string | null;
  postalCode?: string | null;
}

export type BranchType = 'HeadOffice' | 'Branch';

export interface BranchResponse {
  id: string;
  code: string;
  name: string;
  type: BranchType;
  address: AddressDto;
  phoneNumber: string;
  email?: string | null;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
}

export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasNextPage: boolean;
}

/** RFC 9457 problem details returned by the API for every error. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  traceId?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
}
