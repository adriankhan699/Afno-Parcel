import { isNepalPhone } from './booking';
import type { FormErrors } from './booking';

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export interface LoginForm {
  email: string;
  password: string;
}

export interface RegisterForm extends LoginForm {
  fullName: string;
  phoneNumber: string;
}

export function validateLogin(form: LoginForm): FormErrors {
  const errors: FormErrors = {};
  if (!form.email.trim()) errors.email = 'Email is required.';
  else if (!EMAIL_PATTERN.test(form.email.trim())) errors.email = 'Enter a valid email address.';
  if (!form.password) errors.password = 'Password is required.';
  return errors;
}

/** Mirrors the server password policy: 10+ characters with an upper-case letter, a lower-case letter and a digit. */
export function passwordProblem(password: string): string | null {
  if (password.length < 10) return 'Use at least 10 characters.';
  if (!/[a-z]/.test(password)) return 'Add a lower-case letter.';
  if (!/[A-Z]/.test(password)) return 'Add an upper-case letter.';
  if (!/\d/.test(password)) return 'Add a digit.';
  return null;
}

export function validateRegister(form: RegisterForm): FormErrors {
  const errors = validateLogin({ email: form.email, password: '-' });
  delete errors.password;
  if (!form.fullName.trim()) errors.fullName = 'Full name is required.';
  else if (form.fullName.trim().length > 150) errors.fullName = 'Name must be at most 150 characters.';
  if (!form.phoneNumber.trim()) errors.phoneNumber = 'Phone number is required.';
  else if (!isNepalPhone(form.phoneNumber)) errors.phoneNumber = 'Enter a Nepali mobile (98XXXXXXXX) or landline number.';
  const password = passwordProblem(form.password);
  if (password) errors.password = password;
  return errors;
}
