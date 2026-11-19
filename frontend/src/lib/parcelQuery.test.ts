import { describe, expect, it } from 'vitest';
import { emptyFilters, filtersToQuery, isDateRangeInvalid, toQueryString } from './parcelQuery';

describe('filtersToQuery', () => {
  it('sends only paging when no filter is set', () => {
    expect(filtersToQuery(emptyFilters(), 1)).toEqual({ page: 1, pageSize: 20 });
  });

  it('maps every filter, trimming the search and converting COD to a boolean', () => {
    const query = filtersToQuery(
      {
        search: '  AFN123  ',
        status: 'InTransit',
        serviceType: 'Express',
        paymentMethod: 'CashOnDelivery',
        cod: 'yes',
        channel: 'Counter',
        bookedFrom: '2026-11-01',
        bookedTo: '2026-11-18',
      },
      3,
    );

    expect(query).toEqual({
      page: 3,
      pageSize: 20,
      search: 'AFN123',
      status: 'InTransit',
      serviceType: 'Express',
      paymentMethod: 'CashOnDelivery',
      isCod: true,
      channel: 'Counter',
      bookedFrom: '2026-11-01',
      bookedTo: '2026-11-18',
    });
  });

  it('maps "no COD" to false (not omitted) and ignores malformed dates', () => {
    const query = filtersToQuery({ ...emptyFilters(), cod: 'no', bookedFrom: '18/11/2026', bookedTo: '' }, 1);
    expect(query.isCod).toBe(false);
    expect(query.bookedFrom).toBeUndefined();
    expect(query.bookedTo).toBeUndefined();
  });
});

describe('toQueryString', () => {
  it('builds a percent-encoded query string and keeps false values', () => {
    expect(toQueryString({ page: 1, pageSize: 20, search: 'Sita Sharma & co', isCod: false })).toBe(
      '?page=1&pageSize=20&search=Sita+Sharma+%26+co&isCod=false',
    );
  });

  it('skips undefined and empty values', () => {
    expect(toQueryString({ page: 2, search: '', status: undefined })).toBe('?page=2');
    expect(toQueryString({})).toBe('');
  });
});

describe('isDateRangeInvalid', () => {
  it('detects a back-to-front range only when both dates are set', () => {
    expect(isDateRangeInvalid({ bookedFrom: '2026-11-18', bookedTo: '2026-11-01' })).toBe(true);
    expect(isDateRangeInvalid({ bookedFrom: '2026-11-01', bookedTo: '2026-11-01' })).toBe(false);
    expect(isDateRangeInvalid({ bookedFrom: '2026-11-18', bookedTo: '' })).toBe(false);
  });
});
