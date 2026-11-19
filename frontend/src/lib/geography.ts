import { PROVINCES, type ServiceAreaResponse } from '../types/api';

export interface DistrictOption {
  district: string;
  isServiceable: boolean;
  estimatedDeliveryDays: number;
}

/** Provinces that appear in the service areas, in constitutional order (unknown ones last). */
export function provincesFrom(areas: readonly ServiceAreaResponse[]): string[] {
  const present = new Set(areas.map((a) => a.province));
  const known: string[] = PROVINCES.filter((p) => present.has(p));
  const unknown = [...present].filter((p) => !known.includes(p)).sort();
  return [...known, ...unknown];
}

/**
 * Districts of a province, A–Z. A district is listed once even if it also has municipality-level areas;
 * its district-wide area decides whether it is serviceable (otherwise any serviceable area in it counts).
 */
export function districtsFor(areas: readonly ServiceAreaResponse[], province: string): DistrictOption[] {
  const byDistrict = new Map<string, DistrictOption & { wholeDistrict: boolean }>();
  for (const area of areas) {
    if (area.province !== province) continue;
    const wholeDistrict = !area.municipality;
    const existing = byDistrict.get(area.district);
    if (!existing || (wholeDistrict && !existing.wholeDistrict)) {
      byDistrict.set(area.district, {
        district: area.district,
        isServiceable: area.isServiceable,
        estimatedDeliveryDays: area.estimatedDeliveryDays,
        wholeDistrict,
      });
    } else if (!existing.wholeDistrict && area.isServiceable) {
      existing.isServiceable = true;
    }
  }
  return [...byDistrict.values()]
    .map(({ district, isServiceable, estimatedDeliveryDays }) => ({ district, isServiceable, estimatedDeliveryDays }))
    .sort((a, b) => a.district.localeCompare(b.district));
}
