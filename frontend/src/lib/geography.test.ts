import { describe, expect, it } from 'vitest';
import type { ServiceAreaResponse } from '../types/api';
import { districtsFor, provincesFrom } from './geography';

const area = (province: string, district: string, over: Partial<ServiceAreaResponse> = {}): ServiceAreaResponse => ({
  id: `${province}-${district}-${over.municipality ?? ''}`,
  code: 'X',
  name: district,
  province,
  district,
  municipality: null,
  isServiceable: true,
  estimatedDeliveryDays: 2,
  servingBranchId: null,
  ...over,
});

describe('provincesFrom', () => {
  it('lists provinces in constitutional order, once each', () => {
    const areas = [area('Gandaki', 'Kaski'), area('Koshi', 'Morang'), area('Gandaki', 'Syangja'), area('Bagmati', 'Kathmandu')];
    expect(provincesFrom(areas)).toEqual(['Koshi', 'Bagmati', 'Gandaki']);
  });

  it('puts unknown provinces last', () => {
    expect(provincesFrom([area('Atlantis', 'X'), area('Koshi', 'Morang')])).toEqual(['Koshi', 'Atlantis']);
  });
});

describe('districtsFor', () => {
  it('returns the province’s districts A–Z with serviceability', () => {
    const areas = [
      area('Bagmati', 'Lalitpur'),
      area('Bagmati', 'Kathmandu'),
      area('Bagmati', 'Rasuwa', { isServiceable: false }),
      area('Koshi', 'Morang'),
    ];
    expect(districtsFor(areas, 'Bagmati').map((d) => [d.district, d.isServiceable])).toEqual([
      ['Kathmandu', true],
      ['Lalitpur', true],
      ['Rasuwa', false],
    ]);
  });

  it('lists a district once and lets its district-wide area decide serviceability', () => {
    const areas = [
      area('Bagmati', 'Kathmandu', { municipality: 'Tokha', isServiceable: true }),
      area('Bagmati', 'Kathmandu', { municipality: null, isServiceable: false }),
    ];
    const result = districtsFor(areas, 'Bagmati');
    expect(result).toHaveLength(1);
    expect(result[0]?.isServiceable).toBe(false);
  });

  it('is empty for an unknown province', () => {
    expect(districtsFor([area('Koshi', 'Morang')], 'Gandaki')).toEqual([]);
  });
});
