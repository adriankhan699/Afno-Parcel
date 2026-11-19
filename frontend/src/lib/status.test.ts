import { describe, expect, it } from 'vitest';
import { PARCEL_STATUSES } from '../types/api';
import { STATUS_LABELS, statusLabel, statusTone } from './labels';
import { HAPPY_PATH, computeProgress, isSpecialStatus, timelineFromHistory, timelineFromTracking } from './status';

describe('status labels', () => {
  it('has a label for exactly the ten statuses', () => {
    expect(Object.keys(STATUS_LABELS).sort()).toEqual([...PARCEL_STATUSES].sort());
    expect(PARCEL_STATUSES).toHaveLength(10);
  });

  it.each([
    ['BookingCreated', 'Booking created'],
    ['PickedUp', 'Picked up'],
    ['Shipped', 'Shipped'],
    ['InTransit', 'In transit'],
    ['ArrivedAtWarehouse', 'Arrived at warehouse'],
    ['OutForDelivery', 'Out for delivery'],
    ['Delivered', 'Delivered'],
    ['DeliveryFailed', 'Delivery attempt failed'],
    ['Returned', 'Returned'],
    ['Cancelled', 'Cancelled'],
  ])('%s → %s', (code, label) => {
    expect(statusLabel(code)).toBe(label);
  });

  it('no longer knows the removed statuses and falls back to the raw code', () => {
    for (const removed of ['Booked', 'ReceivedAtBranch', 'ReceivedAtWarehouse', 'ReturnToSender']) {
      expect(PARCEL_STATUSES).not.toContain(removed);
      expect(statusLabel(removed)).toBe(removed);
    }
  });

  it('colours good, bad and neutral statuses differently', () => {
    expect(statusTone('Delivered')).toContain('emerald');
    expect(statusTone('Cancelled')).toContain('red');
    expect(statusTone('DeliveryFailed')).toContain('red');
    expect(statusTone('BookingCreated')).toContain('slate');
    expect(statusTone('InTransit')).toContain('blue');
  });
});

describe('computeProgress', () => {
  const states = (status: string, history: string[] = []) => computeProgress(status, history).steps.map((s) => s.state);

  it('the happy path has the seven expected steps', () => {
    expect(HAPPY_PATH).toEqual(['BookingCreated', 'PickedUp', 'Shipped', 'InTransit', 'ArrivedAtWarehouse', 'OutForDelivery', 'Delivered']);
  });

  it('a new booking is on the first step', () => {
    expect(states('BookingCreated')).toEqual(['current', 'upcoming', 'upcoming', 'upcoming', 'upcoming', 'upcoming', 'upcoming']);
    expect(computeProgress('BookingCreated').percent).toBe(14);
  });

  it('marks earlier steps done and the current one', () => {
    expect(states('InTransit')).toEqual(['done', 'done', 'done', 'current', 'upcoming', 'upcoming', 'upcoming']);
    expect(computeProgress('InTransit').percent).toBe(57);
  });

  it('a delivered parcel has every step done and is 100%', () => {
    expect(states('Delivered')).toEqual(['done', 'done', 'done', 'done', 'done', 'done', 'done']);
    expect(computeProgress('Delivered').percent).toBe(100);
    expect(computeProgress('Delivered').special).toBeNull();
  });

  it('a failed delivery keeps the steps it reached and reports the special state', () => {
    const history = ['BookingCreated', 'PickedUp', 'Shipped', 'InTransit', 'ArrivedAtWarehouse', 'OutForDelivery', 'DeliveryFailed'];
    const progress = computeProgress('DeliveryFailed', history);

    expect(progress.special).toBe('DeliveryFailed');
    expect(progress.steps.map((s) => s.state)).toEqual(['done', 'done', 'done', 'done', 'done', 'done', 'upcoming']);
    expect(progress.steps.some((s) => s.state === 'current')).toBe(false);
  });

  it('a cancelled booking shows how far it got', () => {
    const progress = computeProgress('Cancelled', ['BookingCreated', 'PickedUp', 'Cancelled']);

    expect(progress.special).toBe('Cancelled');
    expect(progress.steps.map((s) => s.state)).toEqual(['done', 'done', 'upcoming', 'upcoming', 'upcoming', 'upcoming', 'upcoming']);
    expect(progress.percent).toBe(29);
  });

  it('a returned parcel is a special end state', () => {
    expect(computeProgress('Returned', ['BookingCreated', 'PickedUp', 'Shipped', 'DeliveryFailed', 'Returned']).special).toBe('Returned');
  });

  it('copes with no history and with unknown statuses', () => {
    expect(computeProgress('Cancelled').percent).toBe(0);
    const unknown = computeProgress('SomethingNew', ['BookingCreated']);
    expect(unknown.special).toBeNull();
    expect(unknown.steps[0]?.state).toBe('done');
  });

  it('recognises the special statuses', () => {
    expect(isSpecialStatus('DeliveryFailed')).toBe(true);
    expect(isSpecialStatus('Returned')).toBe(true);
    expect(isSpecialStatus('Cancelled')).toBe(true);
    expect(isSpecialStatus('Delivered')).toBe(false);
  });
});

describe('timelines', () => {
  it('shows the full history oldest first with from → to, place, staff name and remarks', () => {
    const items = timelineFromHistory([
      { status: 'Shipped', statusName: 'Shipped', occurredAtUtc: '2026-11-18T10:00:00Z', fromStatus: 'PickedUp', location: 'Kathmandu Main', changedBy: 'Hari' },
      { status: 'BookingCreated', statusName: 'Booking created', occurredAtUtc: '2026-11-18T08:00:00Z', fromStatus: null, remarks: 'Parcel booked' },
      { status: 'PickedUp', statusName: 'Picked up', occurredAtUtc: '2026-11-18T09:00:00Z', fromStatus: 'BookingCreated' },
    ]);

    expect(items.map((i) => i.title)).toEqual(['Booking created', 'Picked up', 'Shipped']);
    expect(items[0]?.transition).toBeUndefined();
    expect(items[0]?.remarks).toBe('Parcel booked');
    expect(items[1]?.transition).toBe('Booking created → Picked up');
    expect(items[2]).toMatchObject({ transition: 'Picked up → Shipped', location: 'Kathmandu Main', changedBy: 'Hari' });
    expect(new Set(items.map((i) => i.key)).size).toBe(3);
  });

  it('shows public tracking newest first with the standard message and no staff details', () => {
    const items = timelineFromTracking([
      { status: 'BookingCreated', statusName: 'Booking created', message: 'Your booking has been created.', occurredAtUtc: '2026-11-18T08:00:00Z' },
      { status: 'PickedUp', statusName: 'Picked up', message: 'The parcel has been picked up.', occurredAtUtc: '2026-11-18T09:00:00Z', location: 'Kathmandu Main' },
    ]);

    expect(items.map((i) => i.title)).toEqual(['Picked up', 'Booking created']);
    expect(items[0]?.message).toBe('The parcel has been picked up.');
    expect(items[0]?.location).toBe('Kathmandu Main');
    expect(items[0]?.remarks).toBeUndefined();
    expect(items[0]?.changedBy).toBeUndefined();
  });

  it('passes remarks through only when the detailed event has them', () => {
    const items = timelineFromTracking([
      { status: 'DeliveryFailed', statusName: 'Delivery attempt failed', message: 'x', occurredAtUtc: '2026-11-18T09:00:00Z', remarks: 'Phone off' },
      { status: 'OutForDelivery', statusName: 'Out for delivery', message: 'y', occurredAtUtc: '2026-11-18T08:00:00Z' },
    ]);

    expect(items[0]?.remarks).toBe('Phone off');
    expect(items[1]?.remarks).toBeUndefined();
  });

  it('is fine with an empty history', () => {
    expect(timelineFromHistory([])).toEqual([]);
    expect(timelineFromTracking([])).toEqual([]);
  });
});
