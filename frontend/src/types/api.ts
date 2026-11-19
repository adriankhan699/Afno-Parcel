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
  warehouseCount: number;
  staffCount: number;
}

export interface CreateBranchRequest {
  code: string;
  name: string;
  type: BranchType;
  address: AddressDto;
  phoneNumber: string;
  email?: string | null;
}

export interface UpdateBranchRequest {
  name: string;
  address: AddressDto;
  phoneNumber: string;
  email?: string | null;
}

export interface BranchQuery {
  page?: number;
  pageSize?: number;
  search?: string;
  type?: string;
  includeInactive?: boolean;
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
  /** Ticked "update me by SMS". Sent with the booking so the booking message itself follows the choice. Omitted when not ticked. */
  notifyBySms?: boolean | null;
  notifyByWhatsApp?: boolean | null;
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
  /** For InTransit leaving a warehouse: the warehouse the parcel is travelling to. */
  toWarehouseId?: string;
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
  /** Whatever the scanner read: a tracking number (barcode) or a tracking link (QR code). */
  trackingNumber: string;
  scanType: ScanType;
  warehouseId: string;
  remarks?: string;
  toWarehouseId?: string;
}

export interface BatchScanRequest {
  scanType: ScanType;
  warehouseId: string;
  trackingNumbers: string[];
  remarks?: string;
  toWarehouseId?: string;
}

/** Applied = a new movement was recorded; Duplicate = the same scan was already recorded; Failed = refused. */
export const SCAN_OUTCOMES = ['Applied', 'Duplicate', 'Failed'] as const;
export type ScanOutcome = (typeof SCAN_OUTCOMES)[number];

export interface ScanResultDto {
  trackingNumber: string;
  outcome: ScanOutcome | string;
  /** True unless the scan failed. */
  succeeded: boolean;
  status?: string | null;
  statusName?: string | null;
  message?: string | null;
  warehouse?: string | null;
  toWarehouse?: string | null;
  scannedAtUtc?: string | null;
  error?: string | null;
}

export interface BatchScanResponse {
  results: ScanResultDto[];
  /** Scans that recorded a new movement. */
  succeeded: number;
  duplicates: number;
  failed: number;
}

export interface ParcelMovementResponse {
  id: string;
  scanType: string;
  warehouseId: string;
  warehouseName: string;
  toWarehouseId?: string | null;
  toWarehouseName?: string | null;
  staffName?: string | null;
  occurredAtUtc: string;
  remarks?: string | null;
  isUnexpectedArrival: boolean;
}

export interface ParcelScanResponse {
  id: string;
  parcelId: string;
  trackingNumber: string;
  warehouseId: string;
  warehouseName: string;
  staffUserId: string;
  staffName?: string | null;
  scanType: string;
  outcome: string;
  scannedAtUtc: string;
  remarks?: string | null;
  movementId: string;
}

export interface ScanLogQuery {
  page?: number;
  pageSize?: number;
  warehouseId?: string;
  trackingNumber?: string;
  scanType?: string;
  outcome?: string;
  staffUserId?: string;
  from?: string;
  to?: string;
}

export interface ParcelLabelResponse {
  trackingNumber: string;
  /** data:image/svg+xml;base64,... for an img tag; never inject as HTML. */
  barcodeDataUri: string;
  qrDataUri: string;
  qrContent: string;
  serviceType: string;
  quantity: number;
  weightKg: number;
  originBranch: string;
  destinationArea: string;
  receiverName: string;
  receiverArea: string;
  codAmount: number;
  bookedAtUtc: string;
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
  branchName: string;
  parcelsHeld: number;
  parcelsInbound: number;
}

export interface CreateWarehouseRequest {
  branchId: string;
  code: string;
  name: string;
  address: AddressDto;
  phoneNumber: string;
  capacityParcels?: number | null;
}

export interface UpdateWarehouseRequest {
  name: string;
  address: AddressDto;
  phoneNumber: string;
  capacityParcels?: number | null;
}

export interface WarehouseQuery {
  page?: number;
  pageSize?: number;
  search?: string;
  branchId?: string;
  includeInactive?: boolean;
}

export interface InventoryParcelDto {
  parcelId: string;
  trackingNumber: string;
  status: string;
  statusName: string;
  serviceType: string;
  weightKg: number;
  quantity: number;
  destinationDistrict: string;
  /** When it arrived (held) or was dispatched (inbound). */
  sinceUtc: string;
  waitingHours: number;
}

export interface WarehouseInventoryResponse {
  warehouseId: string;
  warehouseName: string;
  capacityParcels?: number | null;
  parcelsHeld: number;
  parcelsInbound: number;
  utilisationPercent?: number | null;
  held: PagedResponse<InventoryParcelDto>;
  inbound: InventoryParcelDto[];
}

export const WAITING_KINDS = ['Pickup', 'Dispatch'] as const;
export type WaitingKind = (typeof WAITING_KINDS)[number];

export interface WaitingParcelDto {
  parcelId: string;
  trackingNumber: string;
  kind: WaitingKind | string;
  status: string;
  statusName: string;
  location: string;
  serviceType: string;
  weightKg: number;
  quantity: number;
  destinationDistrict: string;
  waitingSinceUtc: string;
  waitingHours: number;
}

export interface WaitingQuery {
  page?: number;
  pageSize?: number;
  kind?: string;
  branchId?: string;
  warehouseId?: string;
}

// Staff and assignments (NepalParcel.Contracts.Staff)

export interface StaffAssignmentDto {
  assignmentId: string;
  branchId: string;
  branchName: string;
  warehouseId?: string | null;
  warehouseName?: string | null;
  assignedAtUtc: string;
}

export interface StaffMemberResponse {
  userId: string;
  fullName: string;
  email: string;
  roles: string[];
  isActive: boolean;
  assignment?: StaffAssignmentDto | null;
}

export interface AssignStaffRequest {
  branchId: string;
  warehouseId?: string | null;
}

export interface StaffQuery {
  page?: number;
  pageSize?: number;
  search?: string;
  role?: string;
  branchId?: string;
  warehouseId?: string;
  unassigned?: boolean;
}

export interface MyAssignmentResponse {
  assignment?: StaffAssignmentDto | null;
}

export const STAFF_ROLES = ['SuperAdmin', 'BranchManager', 'BookingStaff', 'WarehouseStaff', 'DeliveryAgent', 'Accountant'] as const;

/** POST /api/v1/admin/users (SuperAdmin). */
export interface CreateStaffUserRequest {
  fullName: string;
  email: string;
  phoneNumber?: string | null;
  password: string;
  role: string;
}

// ---------------------------------------------------------------------------------------------
// Delivery agents (NepalParcel.Contracts.Delivery)

export interface CreateDeliveryAgentRequest {
  fullName: string;
  email: string;
  phoneNumber: string;
  password: string;
  branchId: string;
  vehicleType?: string | null;
  vehicleNumber?: string | null;
  licenseNumber?: string | null;
}

export interface UpdateDeliveryAgentRequest {
  phoneNumber: string;
  vehicleType?: string | null;
  vehicleNumber?: string | null;
  licenseNumber?: string | null;
}

export interface SetAvailabilityRequest {
  isAvailable: boolean;
}

export interface DeliveryAgentResponse {
  id: string;
  userId: string;
  fullName: string;
  email?: string | null;
  phoneNumber: string;
  branchId: string;
  branchName: string;
  vehicleType?: string | null;
  vehicleNumber?: string | null;
  licenseNumber?: string | null;
  isActive: boolean;
  isAvailable: boolean;
  openAssignments: number;
  createdAtUtc: string;
}

export interface DeliveryAgentQuery {
  page?: number;
  pageSize?: number;
  search?: string;
  branchId?: string;
  isActive?: boolean;
  isAvailable?: boolean;
}

export interface AssignDeliveryRequest {
  parcelId: string;
  deliveryAgentId: string;
}

export interface CancelAssignmentRequest {
  reason: string;
}

export const ASSIGNMENT_STATUSES = ['Assigned', 'Accepted', 'InProgress', 'Completed', 'Failed', 'Cancelled'] as const;
export type AssignmentStatus = (typeof ASSIGNMENT_STATUSES)[number];

export type ProofType = 'Signature' | 'Photo' | 'Otp';

export interface DeliveryProofResponse {
  id: string;
  type: ProofType | string;
  recipientName?: string | null;
  otpVerified: boolean;
  hasFile: boolean;
  capturedAtUtc: string;
}

export interface DeliveryAssignmentResponse {
  id: string;
  parcelId: string;
  trackingNumber: string;
  deliveryAgentId: string;
  agentName: string;
  type: string;
  status: AssignmentStatus | string;
  attemptNumber: number;
  assignedAtUtc: string;
  startedAtUtc?: string | null;
  completedAtUtc?: string | null;
  failureReason?: string | null;
  parcelStatus: string;
  receiverName: string;
  receiverDistrict: string;
  codAmount: number;
  codCollectedAmount?: number | null;
  proofs: DeliveryProofResponse[];
}

export interface DeliveryAssignmentQuery {
  page?: number;
  pageSize?: number;
  deliveryAgentId?: string;
  parcelId?: string;
  branchId?: string;
  /** An assignment status, or 'Open' for assigned, accepted and in progress. */
  status?: string;
  from?: string;
  to?: string;
}

export interface ReadyParcelResponse {
  parcelId: string;
  trackingNumber: string;
  status: string;
  statusName: string;
  location: string;
  receiverName: string;
  receiverDistrict: string;
  receiverMunicipality: string;
  serviceType: string;
  codAmount: number;
  failedAttempts: number;
  waitingSinceUtc: string;
  waitingHours: number;
}

export interface ReadyParcelQuery {
  page?: number;
  pageSize?: number;
  branchId?: string;
  search?: string;
}

export interface AgentSummaryResponse {
  agentId: string;
  fullName: string;
  branchName: string;
  isAvailable: boolean;
  openAssignments: number;
  deliveredToday: number;
  failedToday: number;
  codCollectedToday: number;
  codCollectedUnsettled: number;
}

export interface AgentAssignmentResponse {
  id: string;
  parcelId: string;
  trackingNumber: string;
  status: AssignmentStatus | string;
  attemptNumber: number;
  assignedAtUtc: string;
  startedAtUtc?: string | null;
  completedAtUtc?: string | null;
  failureReason?: string | null;
  parcelStatus: string;
  receiverName: string;
  /** Only while the assignment is open. */
  receiverPhone?: string | null;
  /** Only while the assignment is open. */
  receiverAddress?: string | null;
  receiverInstructions?: string | null;
  receiverArea: string;
  serviceType: string;
  description: string;
  quantity: number;
  weightKg: number;
  codAmount: number;
  codCollected: boolean;
  codCollectedAmount?: number | null;
  hasProof: boolean;
  proofs: DeliveryProofResponse[];
  canStart: boolean;
  canFail: boolean;
  canComplete: boolean;
  canReturnToWarehouse: boolean;
}

export interface AgentAssignmentQuery {
  page?: number;
  pageSize?: number;
  /** 'open' (default) or 'history'. */
  scope?: string;
}

export interface MarkDeliveryFailedRequest {
  reason: string;
  remarks?: string | null;
}

export interface CollectCodRequest {
  collectedAmount: number;
}

export interface CodCollectionResponse {
  id: string;
  parcelId: string;
  expectedAmount: number;
  collectedAmount: number;
  collectedAtUtc: string;
}

export interface DeliveryCodeSentResponse {
  message: string;
  validForMinutes: number;
}

export interface VerifyDeliveryCodeRequest {
  code: string;
  recipientName?: string | null;
}

export interface CompleteDeliveryRequest {
  remarks?: string | null;
}

export interface ReturnToWarehouseRequest {
  warehouseId: string;
  remarks?: string | null;
}

export interface AgentPerformanceQuery {
  from?: string;
  to?: string;
  branchId?: string;
  deliveryAgentId?: string;
}

export interface AgentPerformanceRow {
  deliveryAgentId: string;
  fullName: string;
  branchName: string;
  isActive: boolean;
  assigned: number;
  delivered: number;
  failed: number;
  cancelled: number;
  inProgress: number;
  successRatePercent?: number | null;
  firstAttemptSuccessPercent?: number | null;
  averageDeliveryHours?: number | null;
  codCollections: number;
  codCollected: number;
}

export interface AgentPerformanceReport {
  from: string;
  to: string;
  agents: AgentPerformanceRow[];
  totals: AgentPerformanceRow;
}

// ---------------------------------------------------------------------------------------------
// Payments (NepalParcel.Contracts.Payments). Test mode: the online providers are simulated by the server.

/** The online providers a parcel can be paid with. */
export const ONLINE_PROVIDERS = ['Esewa', 'Khalti'] as const;
export type OnlineProvider = (typeof ONLINE_PROVIDERS)[number];

export interface InitiatePaymentRequest {
  parcelId: string;
  provider: OnlineProvider;
}

export interface InitiatePaymentResponse {
  paymentId: string;
  /** Our own merchant reference for this attempt. */
  reference: string;
  provider: string;
  amount: number;
  currency: string;
  redirectUrl: string;
  expiresAtUtc: string;
  testMode: boolean;
}

export interface PaymentCallbackRequest {
  reference: string;
  providerData?: Record<string, string>;
}

/** Transaction status as decided by the server after asking the provider: Pending, Succeeded or Failed. */
export interface PaymentStatusResponse {
  reference: string;
  provider: string;
  status: string;
  amount: number;
  currency: string;
  trackingNumber: string;
  paymentStatus: string;
  failureReason?: string | null;
  completedAtUtc?: string | null;
}

export const TEST_GATEWAY_OUTCOMES = ['Success', 'Failure', 'Cancel'] as const;
export type TestGatewayOutcome = (typeof TEST_GATEWAY_OUTCOMES)[number];

export interface TestGatewaySessionResponse {
  reference: string;
  provider: string;
  merchantName: string;
  amount: number;
  currency: string;
  status: string;
  providerTransactionId?: string | null;
}

export interface TestGatewayCompleteRequest {
  outcome: TestGatewayOutcome;
}

export interface PaymentTransactionResponse {
  id: string;
  reference: string;
  type: string;
  status: string;
  provider: string;
  providerReference?: string | null;
  amount: number;
  currency: string;
  failureReason?: string | null;
  createdAtUtc: string;
  completedAtUtc?: string | null;
}

export interface PaymentResponse {
  id: string;
  parcelId: string;
  trackingNumber: string;
  amount: number;
  currency: string;
  method: string;
  status: string;
  paidAtUtc?: string | null;
  reference?: string | null;
  receiptNumber?: string | null;
  transactions: PaymentTransactionResponse[];
}

export interface PaymentReceiptResponse {
  receiptNumber: string;
  companyName: string;
  companyPhone?: string | null;
  companyTaxNumber?: string | null;
  trackingNumber: string;
  payerName: string;
  description: string;
  amount: number;
  currency: string;
  method: string;
  provider: string;
  providerReference?: string | null;
  paidAtUtc: string;
  receivedBy?: string | null;
}

export interface RecordOfficeCashRequest {
  parcelId: string;
  amountReceived: number;
  note?: string | null;
}

export interface PaymentTransactionRow {
  id: string;
  paymentId: string;
  parcelId: string;
  trackingNumber: string;
  reference: string;
  type: string;
  status: string;
  provider: string;
  providerReference?: string | null;
  amount: number;
  currency: string;
  failureReason?: string | null;
  createdAtUtc: string;
  completedAtUtc?: string | null;
}

export interface PaymentTransactionQuery {
  page?: number;
  pageSize?: number;
  provider?: string;
  status?: string;
  parcelId?: string;
  branchId?: string;
  search?: string;
  from?: string;
  to?: string;
}

export interface CreateSettlementRequest {
  remarks?: string | null;
}

export interface VerifySettlementRequest {
  countedAmount: number;
  remarks?: string | null;
}

export interface ResolveSettlementRequest {
  remarks: string;
}

export interface SettlementCollectionDto {
  parcelId: string;
  trackingNumber: string;
  expectedAmount: number;
  collectedAmount: number;
  collectedAtUtc: string;
  status: string;
}

export interface SettlementResponse {
  id: string;
  referenceNumber: string;
  branchId: string;
  branchName: string;
  agentId?: string | null;
  agentName?: string | null;
  totalAmount: number;
  countedAmount?: number | null;
  variance?: number | null;
  collectionCount: number;
  /** Submitted, Verified or Disputed. */
  status: string;
  createdAtUtc: string;
  submittedAtUtc?: string | null;
  verifiedAtUtc?: string | null;
  remarks?: string | null;
  collections: SettlementCollectionDto[];
}

export interface SettlementQuery {
  page?: number;
  pageSize?: number;
  status?: string;
  agentId?: string;
  branchId?: string;
  from?: string;
  to?: string;
}

export interface AgentCashPositionRow {
  agentId: string;
  fullName: string;
  branchName: string;
  collected: number;
  inHand: number;
  inSettlement: number;
  disputed: number;
  settled: number;
  collectionCount: number;
  lastCollectionAtUtc?: string | null;
}

export interface AgentCashQuery {
  branchId?: string;
  agentId?: string;
}

export interface AgentCashSummaryResponse {
  position: AgentCashPositionRow;
  recentSettlements: SettlementResponse[];
}

export interface PaymentReportQuery {
  from?: string;
  to?: string;
  branchId?: string;
}

export interface MethodTotal {
  method: string;
  count: number;
  amount: number;
}

export interface ProviderTotal {
  provider: string;
  succeeded: number;
  failed: number;
  pending: number;
  amount: number;
}

export interface DailyTotal {
  date: string;
  count: number;
  amount: number;
}

export interface CodReport {
  collectionCount: number;
  collected: number;
  inHand: number;
  inSettlement: number;
  disputed: number;
  settled: number;
}

export interface SettlementReport {
  submitted: number;
  verified: number;
  disputed: number;
  totalVariance: number;
}

export interface PaymentReportResponse {
  from: string;
  to: string;
  branchId?: string | null;
  paidCount: number;
  paidAmount: number;
  pendingCount: number;
  pendingAmount: number;
  failedAttempts: number;
  byMethod: MethodTotal[];
  byProvider: ProviderTotal[];
  daily: DailyTotal[];
  cod: CodReport;
  settlements: SettlementReport;
}

// ---------------------------------------------------------------------------------------------
// Notifications (SMS and WhatsApp). Mirrors NepalParcel.Contracts.Notifications; JSON is camelCase and enums are strings.

export const NOTIFICATION_CHANNELS = ['Sms', 'WhatsApp'] as const;
export type NotificationChannel = (typeof NOTIFICATION_CHANNELS)[number];

export const NOTIFICATION_AUDIENCES = ['Sender', 'Receiver'] as const;
export type NotificationAudience = (typeof NOTIFICATION_AUDIENCES)[number];

/** What WE have done with the message (our processing state). Never says anything about delivery. */
export const NOTIFICATION_STATUSES = ['Pending', 'Processing', 'Submitted', 'Failed', 'DeadLetter', 'Skipped', 'Cancelled'] as const;
export type NotificationStatus = (typeof NOTIFICATION_STATUSES)[number];

/** Only what the PROVIDER reported afterwards. `None` until it reports. */
export const NOTIFICATION_DELIVERY_STATUSES = ['None', 'Sent', 'Delivered', 'Read', 'Failed'] as const;
export type NotificationDeliveryStatus = (typeof NOTIFICATION_DELIVERY_STATUSES)[number];

export interface NotificationMessageResponse {
  id: string;
  channel: NotificationChannel;
  audience: NotificationAudience;
  templateKey: string;
  recipientMasked: string;
  status: NotificationStatus;
  deliveryStatus: NotificationDeliveryStatus;
  provider?: string | null;
  providerMessageId?: string | null;
  parcelId?: string | null;
  trackingNumber?: string | null;
  attemptCount: number;
  maxAttempts: number;
  createdAtUtc: string;
  nextAttemptAtUtc: string;
  submittedAtUtc?: string | null;
  sentAtUtc?: string | null;
  deliveredAtUtc?: string | null;
  readAtUtc?: string | null;
  failedAtUtc?: string | null;
  skipReason?: string | null;
  lastError?: string | null;
  deliveryError?: string | null;
}

export interface NotificationAttemptResponse {
  id: string;
  status: string;
  provider: string;
  providerMessageId?: string | null;
  attemptNumber?: number | null;
  detail?: string | null;
  recordedAtUtc: string;
  occurredAtUtc?: string | null;
}

export interface NotificationMessageDetailResponse {
  message: NotificationMessageResponse;
  renderedBody?: string | null;
  attempts: NotificationAttemptResponse[];
}

export interface NotificationMessageQuery {
  page?: number;
  pageSize?: number;
  channel?: string;
  status?: string;
  deliveryStatus?: string;
  audience?: string;
  templateKey?: string;
  parcelId?: string;
  search?: string;
  from?: string;
  to?: string;
}

export interface NotificationChannelSummary {
  channel: NotificationChannel;
  total: number;
  queued: number;
  retrying: number;
  submitted: number;
  sent: number;
  delivered: number;
  read: number;
  failed: number;
  skipped: number;
}

export interface NotificationSummaryResponse {
  from: string;
  to: string;
  channels: NotificationChannelSummary[];
  deadLetters: number;
}

export interface RequeueNotificationRequest {
  reason?: string | null;
}

export interface NotificationTemplateResponse {
  id: string;
  key: string;
  channel: NotificationChannel;
  language: string;
  body: string;
  providerTemplateName?: string | null;
  parameters: string[];
  isActive: boolean;
  length: number;
  updatedAtUtc: string;
}

export interface NotificationTemplatesResponse {
  templates: NotificationTemplateResponse[];
  /** Template key -> the placeholder names that may be used in that template's body. */
  allowedPlaceholders: Record<string, string[]>;
}

export interface UpdateNotificationTemplateRequest {
  body: string;
  providerTemplateName?: string | null;
  isActive: boolean;
}

export interface PreviewTemplateRequest {
  key: string;
  body: string;
}

export interface PreviewTemplateResponse {
  text: string;
  parameters: string[];
  problems: string[];
}

export interface NotificationPreferencesResponse {
  sms: boolean;
  whatsApp: boolean;
  smsDecidedAtUtc?: string | null;
  whatsAppDecidedAtUtc?: string | null;
}

/** Changes only what is given. */
export interface UpdateNotificationPreferencesRequest {
  sms?: boolean | null;
  whatsApp?: boolean | null;
}

export interface OptOutResponse {
  id: string;
  phoneMasked: string;
  channel: NotificationChannel;
  source: string;
  createdAtUtc: string;
  removedAtUtc?: string | null;
}

export interface CreateOptOutRequest {
  phoneNumber: string;
  channel: NotificationChannel;
}

export interface OptOutQuery {
  page?: number;
  pageSize?: number;
  channel?: string;
  activeOnly?: boolean;
}

export interface NotificationProviderInfo {
  channel: NotificationChannel;
  provider: string;
  isMock: boolean;
  isConfigured: boolean;
  detail: string;
}

export interface MockProviderMessageResponse {
  id: string;
  channel: NotificationChannel;
  providerMessageId: string;
  toMasked: string;
  body: string;
  templateName?: string | null;
  parameters: string[];
  createdAtUtc: string;
}

export const SIMULATED_STATUSES = ['Sent', 'Delivered', 'Read', 'Failed'] as const;
export type SimulatedStatus = (typeof SIMULATED_STATUSES)[number];

export interface SimulateStatusRequest {
  status: SimulatedStatus;
  error?: string | null;
}
