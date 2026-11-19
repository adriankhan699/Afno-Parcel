import { SelectField, TextAreaField, TextField } from './form';
import { LIMITS, type FormErrors, type PartyForm } from '../lib/booking';
import { districtsFor, provincesFrom } from '../lib/geography';
import type { ServiceAreaResponse } from '../types/api';

interface PartyFieldsProps {
  prefix: 'sender' | 'receiver';
  /** "Sender" / "Receiver", used in labels. */
  who: string;
  value: PartyForm;
  onChange: (next: PartyForm) => void;
  errors: FormErrors;
  areas: readonly ServiceAreaResponse[];
  /** Districts we do not deliver to are shown but cannot be chosen (receiver only). */
  requireServiceable?: boolean;
}

/** Name, phone and Nepal address (province → district → municipality → ward → street) of one party. */
export default function PartyFields({ prefix, who, value, onChange, errors, areas, requireServiceable = false }: PartyFieldsProps) {
  const set = (patch: Partial<PartyForm>) => onChange({ ...value, ...patch });
  const err = (key: string) => errors[`${prefix}.${key}`];

  const provinces = provincesFrom(areas);
  const districts = value.province ? districtsFor(areas, value.province) : [];

  return (
    <div className="grid gap-4 sm:grid-cols-2">
      <TextField
        id={`${prefix}-fullName`}
        label={`${who} full name`}
        required
        maxLength={LIMITS.name}
        autoComplete="off"
        value={value.fullName}
        onChange={(fullName) => set({ fullName })}
        error={err('fullName')}
      />
      <TextField
        id={`${prefix}-phoneNumber`}
        label={`${who} phone number`}
        type="tel"
        inputMode="tel"
        required
        placeholder="98XXXXXXXX"
        autoComplete="off"
        value={value.phoneNumber}
        onChange={(phoneNumber) => set({ phoneNumber })}
        error={err('phoneNumber')}
      />
      <SelectField
        id={`${prefix}-province`}
        label="Province"
        required
        placeholder={areas.length === 0 ? 'Loading…' : 'Choose a province'}
        value={value.province}
        onChange={(province) => set({ province, district: '' })}
        options={provinces.map((p) => ({ value: p, label: p }))}
        error={err('address.province')}
      />
      <SelectField
        id={`${prefix}-district`}
        label="District"
        required
        disabled={!value.province}
        placeholder={value.province ? 'Choose a district' : 'Choose a province first'}
        value={value.district}
        onChange={(district) => set({ district })}
        options={districts.map((d) => ({
          value: d.district,
          label: requireServiceable && !d.isServiceable ? `${d.district} (not served yet)` : d.district,
          disabled: requireServiceable && !d.isServiceable,
        }))}
        error={err('address.district')}
      />
      <TextField
        id={`${prefix}-municipality`}
        label="Municipality / rural municipality"
        required
        maxLength={LIMITS.municipality}
        autoComplete="off"
        value={value.municipality}
        onChange={(municipality) => set({ municipality })}
        error={err('address.municipality')}
      />
      <TextField
        id={`${prefix}-wardNumber`}
        label="Ward number"
        required
        inputMode="numeric"
        placeholder="1–35"
        autoComplete="off"
        value={value.wardNumber}
        onChange={(wardNumber) => set({ wardNumber })}
        error={err('address.wardNumber')}
      />
      <TextField
        id={`${prefix}-streetOrTole`}
        label="Street or locality (tole)"
        maxLength={LIMITS.street}
        autoComplete="off"
        className="sm:col-span-2"
        value={value.streetOrTole}
        onChange={(streetOrTole) => set({ streetOrTole })}
        error={err('address.streetOrTole')}
      />
      <TextAreaField
        id={`${prefix}-addressInstructions`}
        label="Address instructions (optional)"
        rows={2}
        maxLength={LIMITS.instructions}
        placeholder="e.g. Blue gate, call before arriving"
        className="sm:col-span-2"
        value={value.addressInstructions}
        onChange={(addressInstructions) => set({ addressInstructions })}
        error={err('addressInstructions')}
      />
    </div>
  );
}
