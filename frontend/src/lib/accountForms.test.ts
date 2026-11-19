import { describe, expect, it } from 'vitest';
import { passwordProblem, validateLogin, validateRegister } from './accountForms';

describe('validateLogin', () => {
  it('requires a valid email and a password', () => {
    expect(validateLogin({ email: '', password: '' })).toEqual({ email: 'Email is required.', password: 'Password is required.' });
    expect(validateLogin({ email: 'not-an-email', password: 'x' }).email).toBeTruthy();
    expect(validateLogin({ email: 'a@b.co', password: 'x' })).toEqual({});
  });
});

describe('passwordProblem', () => {
  it.each([
    ['Short1a', 'Use at least 10 characters.'],
    ['ALLUPPER1234', 'Add a lower-case letter.'],
    ['alllower1234', 'Add an upper-case letter.'],
    ['NoDigitsHere', 'Add a digit.'],
    ['GoodPass1234', null],
  ])('%s', (password, expected) => expect(passwordProblem(password)).toBe(expected));
});

describe('validateRegister', () => {
  it('accepts valid details', () => {
    expect(validateRegister({ fullName: 'Sita Sharma', email: 'sita@example.com', phoneNumber: '9841234567', password: 'GoodPass1234' })).toEqual({});
  });

  it('reports each bad field', () => {
    const errors = validateRegister({ fullName: '', email: 'x', phoneNumber: '123', password: 'short' });
    expect(Object.keys(errors).sort()).toEqual(['email', 'fullName', 'password', 'phoneNumber']);
  });
});
