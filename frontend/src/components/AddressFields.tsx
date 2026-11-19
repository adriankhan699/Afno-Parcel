import { districtsFor, provincesFrom } from '../lib/geography';
import { LIMITS, type FormErrors } from '../lib/booking';
import type { AddressForm } from '../lib/network';
import type { ServiceAreaResponse } from '../types/api';
import { SelectField, TextField } from './form';

interface AddressFieldsProps {
  /** Prefix for element ids, e.g. "branch". */
  idPrefix: string;
  value: AddressForm;
  onChange: (next: AddressForm) => void;
  errors: FormErrors;
  areas: readonly ServiceAreaResponse[];
}

/** Province → district → municipality → ward → street. Districts come from the service areas the API lists. */
export default function AddressFields({ idPrefix, value, onChange, errors, areas }: AddressFieldsProps) {
  const set = (patch: Partial<AddressForm>) => onChange({ ...value, ...patch });
  const provinces = provincesFrom(areas);
  const districts = value.province ? districtsFor(areas, value.province) : [];

  return (
    <div className="grid gap-4 sm:grid-cols-2">
      <SelectField
        id={`${idPrefix}-province`}
        label="Province"
        required
        placeholder={areas.length === 0 ? 'Loading…' : 'Choose a province'}
        value={value.province}
        onChange={(province) => set({ province, district: '' })}
        options={provinces.map((p) => ({ value: p, label: p }))}
        error={errors['address.province']}
      />
      <SelectField
        id={`${idPrefix}-district`}
        label="District"
        required
        disabled={!value.province}
        placeholder={value.province ? 'Choose a district' : 'Choose a province first'}
        value={value.district}
        onChange={(district) => set({ district })}
        options={districts.map((d) => ({ value: d.district, label: d.district }))}
        error={errors['address.district']}
      />
      <TextField
        id={`${idPrefix}-municipality`}
        label="Municipality / rural municipality"
        required
        maxLength={LIMITS.municipality}
        autoComplete="off"
        value={value.municipality}
        onChange={(municipality) => set({ municipality })}
        error={errors['address.municipality']}
      />
      <TextField
        id={`${idPrefix}-ward`}
        label="Ward number"
        required
        inputMode="numeric"
        placeholder="1–35"
        autoComplete="off"
        value={value.wardNumber}
        onChange={(wardNumber) => set({ wardNumber })}
        error={errors['address.wardNumber']}
      />
      <TextField
        id={`${idPrefix}-street`}
        label="Street or locality (tole)"
        maxLength={LIMITS.street}
        autoComplete="off"
        value={value.streetOrTole}
        onChange={(streetOrTole) => set({ streetOrTole })}
        error={errors['address.streetOrTole']}
      />
      <TextField
        id={`${idPrefix}-postal`}
        label="Postal code (optional)"
        inputMode="numeric"
        maxLength={5}
        autoComplete="off"
        value={value.postalCode}
        onChange={(postalCode) => set({ postalCode })}
        error={errors['address.postalCode']}
      />
    </div>
  );
}
