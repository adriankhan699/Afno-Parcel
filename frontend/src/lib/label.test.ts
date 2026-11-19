import { describe, expect, it } from 'vitest';
import { safeImageSrc } from './label';

describe('safeImageSrc', () => {
  it('accepts base64 SVG and PNG data URIs', () => {
    expect(safeImageSrc('data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=')).toBe('data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=');
    expect(safeImageSrc('  data:image/png;base64,iVBORw0KGgo=  ')).toBe('data:image/png;base64,iVBORw0KGgo=');
  });

  it('refuses everything else', () => {
    expect(safeImageSrc(undefined)).toBeNull();
    expect(safeImageSrc(null)).toBeNull();
    expect(safeImageSrc('')).toBeNull();
    expect(safeImageSrc('https://evil.example/x.svg')).toBeNull();
    expect(safeImageSrc('javascript:alert(1)')).toBeNull();
    expect(safeImageSrc('data:text/html;base64,PHNjcmlwdD4=')).toBeNull();
    expect(safeImageSrc('data:image/svg+xml,<svg onload=alert(1)>')).toBeNull();
    expect(safeImageSrc('data:image/svg+xml;base64,PHN2Zz4="onerror="x')).toBeNull();
  });
});
