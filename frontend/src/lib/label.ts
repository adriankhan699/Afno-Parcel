const SAFE_IMAGE = /^data:image\/(?:svg\+xml|png);base64,[A-Za-z0-9+/]+={0,2}$/;

/**
 * The server sends the barcode and QR code as data: URIs. They are only ever used as an <img> source (an image loaded that
 * way cannot run scripts), and even so anything that is not a base64 SVG or PNG data URI is refused.
 */
export function safeImageSrc(uri: string | null | undefined): string | null {
  if (!uri) return null;
  const text = uri.trim();
  return SAFE_IMAGE.test(text) ? text : null;
}
