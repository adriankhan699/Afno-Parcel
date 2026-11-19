import { describe, expect, it } from 'vitest';
import { problemSummary, problemToFieldErrors, toFieldKey } from './problems';

describe('toFieldKey', () => {
  it('lower-cases the first letter of each segment', () => {
    expect(toFieldKey('Receiver.Address.District')).toBe('receiver.address.district');
    expect(toFieldKey('WeightKg')).toBe('weightKg');
    expect(toFieldKey('Sender.AddressInstructions')).toBe('sender.addressInstructions');
  });
});

describe('problemToFieldErrors', () => {
  it('maps server property paths to form keys and keeps the first message', () => {
    expect(
      problemToFieldErrors({
        errors: {
          'Receiver.Address.District': ['District is required.', 'Second message'],
          WeightKg: ['Weight must be greater than 0.'],
        },
      }),
    ).toEqual({
      'receiver.address.district': 'District is required.',
      weightKg: 'Weight must be greater than 0.',
    });
  });

  it('returns an empty object when there are no field errors', () => {
    expect(problemToFieldErrors(undefined)).toEqual({});
    expect(problemToFieldErrors({ title: 'Conflict' })).toEqual({});
    expect(problemToFieldErrors({ errors: { Empty: [] } })).toEqual({});
  });
});

describe('problemSummary', () => {
  it('counts field errors', () => {
    expect(problemSummary({ errors: { A: ['x'] } })).toBe('Please fix the highlighted field and try again.');
    expect(problemSummary({ errors: { A: ['x'], B: ['y'] } })).toBe('Please fix the 2 highlighted fields and try again.');
  });

  it('falls back to detail, then title, then a default', () => {
    expect(problemSummary({ detail: 'No price is configured.', title: 'Business rule violated' })).toBe('No price is configured.');
    expect(problemSummary({ title: 'Conflict' })).toBe('Conflict');
    expect(problemSummary({})).toBe('Something went wrong. Please try again.');
    expect(problemSummary(undefined, 'custom')).toBe('custom');
  });
});
