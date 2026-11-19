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

// ---------------------------------------------------------------------------------------------
// Auth

export interface CurrentUserResponse {
  id: string;
  email: string;
  fullName: string;
  phoneNumber?: string | null;
  roles: string[];
  createdAtUtc: string;
}

export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
  refreshTokenExpiresAtUtc: string;
  tokenType: string;
  user: CurrentUserResponse;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  fullName: string;
  email: string;
  phoneNumber: string;
  password: string;
}

// ---------------------------------------------------------------------------------------------
// Parcels (NepalParcel.Contracts.Parcels)

export const SERVICE_TYPES = ['Standard', 'Express', 'SameDay'] as const;
export type ServiceType = (typeof SERVICE_TYPES)[number];

export const PREPAID_PAYMENT_METHODS = ['Cash', 'Esewa', 'Khalti', 'ConnectIps', 'BankTransfer'] as const;
export const PAYMENT_METHODS = [...PREPAID_PAYMENT_METHODS, 'CashOnDelivery'] as const;
export type PaymentMethod = (typeof PAYMENT_METHODS)[number];

export const PARCEL_STATUSES = [
  'BookingCreated',
  'PickedUp',
  'Shipped',
  'InTransit',
  'ArrivedAtWarehouse',
  'OutForDelivery',
  'Delivered',
  'DeliveryFailed',
  'Returned',
  'Cancelled',
] as const;
export type ParcelStatus = (typeof PARCEL_STATUSES)[number];

export type BookingChannel = 'Online' | 'Counter';

export interface BookingPartyDto {
  fullName: string;
  phoneNumber: string;
  address: AddressDto;
  addressInstructions?: string | null;
}

export interface BookParcelRequest {
  sender: BookingPartyDto;
  receiver: BookingPartyDto;
  originBranchId: string;
  description: string;
  quantity: number;
  weightKg: number;
  serviceType: ServiceType;
  paymentMethod: PaymentMethod;
  lengthCm?: number | null;
  widthCm?: number | null;
  heightCm?: number | null;
  declaredValue?: number | null;
  codAmount?: number | null;
  paymentReference?: string | null;
  notes?: string | null;
}

export interface QuoteRequest {
  originBranchId: string;
  destination: AddressDto;
  serviceType: ServiceType;
  weightKg: number;
  lengthCm?: number | null;
  widthCm?: number | null;
  heightCm?: number | null;
  codAmount?: number | null;
}

export interface PriceBreakdownDto {
  actualWeightKg: number;
  volumetricWeightKg: number;
  chargeableWeightKg: number;
  baseCharge: number;
  weightCharge: number;
  shippingCharge: number;
  codFee: number;
  vatPercent: number;
  vatAmount: number;
  totalCharge: number;
}

export interface QuoteResponse {
  serviceType: string;
  originArea?: string | null;
  destinationArea: string;
  estimatedDeliveryDays: number;
  estimatedDeliveryAtUtc: string;
  ruleName: string;
  currency: string;
  breakdown: PriceBreakdownDto;
}

export interface BranchSummaryDto {
  id: string;
  code: string;
  name: string;
  phoneNumber: string;
  address: AddressDto;
}

export interface ParcelStatusEventDto {
  status: string;
  statusName: string;
  occurredAtUtc: string;
  remarks?: string | null;
  /** The status before this step; null for the first step. */
  fromStatus?: string | null;
  /** Branch or warehouse name where the step was recorded. */
  location?: string | null;
  /** Staff member who made the change. Only sent to staff. */
  changedBy?: string | null;
}

export interface ParcelSummaryResponse {
  id: string;
  trackingNumber: string;
  status: string;
  statusName: string;
  serviceType: string;
  channel: string;
  paymentMethod: string;
  paymentStatus?: string | null;
  senderName: string;
  receiverName: string;
  receiverDistrict: string;
  receiverMunicipality: string;
  originBranchName: string;
  quantity: number;
  weightKg: number;
  deliveryCharge: number;
  codAmount: number;
  bookedAtUtc: string;
}

export interface ParcelResponse {
  id: string;
  trackingNumber: string;
  status: string;
  statusName: string;
  serviceType: string;
  channel: string;
  sender: BookingPartyDto;
  receiver: BookingPartyDto;
  originBranch: BranchSummaryDto;
  destinationBranch?: BranchSummaryDto | null;
  description: string;
  quantity: number;
  weightKg: number;
  lengthCm?: number | null;
  widthCm?: number | null;
  heightCm?: number | null;
  declaredValue: number;
  paymentMethod: string;
  paymentStatus?: string | null;
  paymentReference?: string | null;
  price: PriceBreakdownDto;
  codAmount: number;
  notes?: string | null;
  bookedAtUtc: string;
  estimatedDeliveryAtUtc?: string | null;
  deliveredAtUtc?: string | null;
  canCancel: boolean;
  history: ParcelStatusEventDto[];
  /** Statuses the signed-in caller may move the parcel to right now (empty for customers and finished parcels). */
  allowedNextStatuses: string[];
}

export interface ParcelSearchQuery {
  page?: number;
  pageSize?: number;
  search?: string;
  status?: string;
  serviceType?: string;
  paymentMethod?: string;
  channel?: string;
  isCod?: boolean;
  originBranchId?: string;
  bookedFrom?: string;
  bookedTo?: string;
}

export interface CancelParcelRequest {
  reason: string;
}

export interface ConfirmPaymentRequest {
  reference?: string | null;
}

export interface ReceiptResponse {
  receiptNumber: string;
  companyName: string;
  companyPhone?: string | null;
  companyTaxNumber?: string | null;
  parcel: ParcelResponse;
}

export interface ServiceAreaResponse {
  id: string;
  code: string;
  name: string;
  province: string;
  district: string;
  municipality?: string | null;
  isServiceable: boolean;
  estimatedDeliveryDays: number;
  servingBranchId?: string | null;
}

// Status updates and warehouse scans (NepalParcel.Contracts.Tracking)

export interface UpdateParcelStatusRequest {
  status: string;
  remarks?: string;
  branchId?: string;
  warehouseId?: string;
}

export interface ParcelStatusUpdateResponse {
  parcelId: string;
  trackingNumber: string;
  previousStatus: string;
  previousStatusName: string;
  status: string;
  statusName: string;
  occurredAtUtc: string;
  location?: string | null;
  nextStatuses: string[];
}

export const SCAN_TYPES = ['Arrival', 'Dispatch', 'OutForDelivery'] as const;
export type ScanType = (typeof SCAN_TYPES)[number];

export interface ScanRequest {
  trackingNumber: string;
  scanType: ScanType;
  warehouseId: string;
  remarks?: string;
}

export interface BatchScanRequest {
  scanType: ScanType;
  warehouseId: string;
  trackingNumbers: string[];
  remarks?: string;
}

export interface ScanResultDto {
  trackingNumber: string;
  succeeded: boolean;
  status?: string | null;
  statusName?: string | null;
  error?: string | null;
}

export interface BatchScanResponse {
  results: ScanResultDto[];
  succeeded: number;
  failed: number;
}

// Public and detailed tracking (NepalParcel.Contracts.Tracking)

export interface TrackingEventDto {
  status: string;
  statusName: string;
  message: string;
  occurredAtUtc: string;
  location?: string | null;
}

export interface PublicTrackingResponse {
  trackingNumber: string;
  status: string;
  statusName: string;
  statusMessage: string;
  serviceType: string;
  originArea: string;
  destinationArea: string;
  bookedAtUtc: string;
  estimatedDeliveryAtUtc?: string | null;
  deliveredAtUtc?: string | null;
  isFinal: boolean;
  events: TrackingEventDto[];
}

export interface DetailedTrackingEventDto extends TrackingEventDto {
  /** Present only for signed-in callers, never for receivers verified by code. */
  remarks?: string | null;
}

export interface DetailedTrackingResponse {
  trackingNumber: string;
  status: string;
  statusName: string;
  statusMessage: string;
  serviceType: string;
  originArea: string;
  destinationArea: string;
  receiverName: string;
  receiverArea: string;
  codAmount: number;
  paymentMethod: string;
  bookedAtUtc: string;
  estimatedDeliveryAtUtc?: string | null;
  deliveredAtUtc?: string | null;
  isFinal: boolean;
  events: DetailedTrackingEventDto[];
}

export interface TrackingOtpRequestedResponse {
  message: string;
  validForMinutes: number;
}

export interface VerifyTrackingOtpRequest {
  code: string;
}

export interface TrackingAccessResponse {
  accessToken: string;
  expiresAtUtc: string;
}

// Warehouses (NepalParcel.Contracts.Warehouses)

export interface WarehouseResponse {
  id: string;
  branchId: string;
  code: string;
  name: string;
  address: AddressDto;
  phoneNumber: string;
  capacityParcels?: number | null;
  isActive: boolean;
}
