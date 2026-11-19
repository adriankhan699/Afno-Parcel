import type { DetailedTrackingEventDto, ParcelStatusEventDto, TrackingEventDto } from '../types/api';
import { statusLabel } from './labels';

// The normal journey of a parcel, in order. DeliveryFailed, Returned and Cancelled are end states shown separately.
export const HAPPY_PATH = [
  'BookingCreated',
  'PickedUp',
  'Shipped',
  'InTransit',
  'ArrivedAtWarehouse',
  'OutForDelivery',
  'Delivered',
] as const;

export type SpecialStatus = 'DeliveryFailed' | 'Returned' | 'Cancelled';

const SPECIAL: readonly string[] = ['DeliveryFailed', 'Returned', 'Cancelled'];

export const isSpecialStatus = (status: string): status is SpecialStatus => SPECIAL.includes(status);

export type StepState = 'done' | 'current' | 'upcoming';

export interface ProgressStep {
  status: string;
  label: string;
  state: StepState;
}

export interface Progress {
  steps: ProgressStep[];
  /** Set when the parcel is in a state that is not on the normal path. */
  special: SpecialStatus | null;
  /** 0–100, how far along the normal path the parcel has got. */
  percent: number;
}

const indexOf = (status: string): number => (HAPPY_PATH as readonly string[]).indexOf(status);

/**
 * Where a parcel is on its journey. For a normal status the steps before it are done and it is the current step
 * (Delivered counts as done). For a failed, returned or cancelled parcel the steps it reached stay done and the
 * special state is reported separately, because the parcel left the normal path.
 */
export function computeProgress(currentStatus: string, historyStatuses: readonly string[] = []): Progress {
  const total = HAPPY_PATH.length;
  const position = indexOf(currentStatus);

  if (position >= 0) {
    const delivered = currentStatus === 'Delivered';
    const steps = HAPPY_PATH.map<ProgressStep>((status, i) => ({
      status,
      label: statusLabel(status),
      state: i < position || (delivered && i === position) ? 'done' : i === position ? 'current' : 'upcoming',
    }));
    return { steps, special: null, percent: Math.round(((position + 1) / total) * 100) };
  }

  const furthest = Math.max(-1, ...historyStatuses.map(indexOf));
  const steps = HAPPY_PATH.map<ProgressStep>((status, i) => ({
    status,
    label: statusLabel(status),
    state: i <= furthest ? 'done' : 'upcoming',
  }));
  return {
    steps,
    special: isSpecialStatus(currentStatus) ? currentStatus : null,
    percent: Math.round(((furthest + 1) / total) * 100),
  };
}

/** One row of a timeline, whichever API it came from. */
export interface TimelineItem {
  key: string;
  title: string;
  /** The standard sentence for the status (tracking views). */
  message?: string;
  /** "Picked up → Shipped" (staff and owner views). */
  transition?: string;
  occurredAtUtc: string;
  location?: string | null;
  remarks?: string | null;
  changedBy?: string | null;
}

const byTimeAscending = <T extends { occurredAtUtc: string }>(a: T, b: T) => a.occurredAtUtc.localeCompare(b.occurredAtUtc);

/** The complete history of a parcel, oldest first. */
export function timelineFromHistory(history: readonly ParcelStatusEventDto[]): TimelineItem[] {
  return [...history].sort(byTimeAscending).map((e, i) => ({
    key: `${e.status}-${e.occurredAtUtc}-${i}`,
    title: e.statusName || statusLabel(e.status),
    transition: e.fromStatus ? `${statusLabel(e.fromStatus)} → ${statusLabel(e.status)}` : undefined,
    occurredAtUtc: e.occurredAtUtc,
    location: e.location,
    remarks: e.remarks,
    changedBy: e.changedBy,
  }));
}

/** Tracking events (public or detailed), newest first, so the latest news is at the top. */
export function timelineFromTracking(events: readonly (TrackingEventDto | DetailedTrackingEventDto)[]): TimelineItem[] {
  return [...events]
    .sort((a, b) => byTimeAscending(b, a))
    .map((e, i) => ({
      key: `${e.status}-${e.occurredAtUtc}-${i}`,
      title: e.statusName || statusLabel(e.status),
      message: e.message,
      occurredAtUtc: e.occurredAtUtc,
      location: e.location,
      remarks: 'remarks' in e ? e.remarks : undefined,
    }));
}
