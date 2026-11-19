export interface DetectedCode {
  rawValue: string;
}

export interface DetectorLike {
  detect(source: HTMLVideoElement): Promise<DetectedCode[]>;
}

export type DetectorConstructor = new (options?: { formats?: string[] }) => DetectorLike;

/** The browser's BarcodeDetector, when it has one (Chrome and Edge today; not Firefox or Safari). */
export function detectorConstructor(): DetectorConstructor | null {
  if (typeof window === 'undefined') return null;
  const candidate = (window as unknown as { BarcodeDetector?: DetectorConstructor }).BarcodeDetector;
  return candidate ?? null;
}

/** True when the browser can read barcodes and QR codes from the camera. */
export function cameraScanningSupported(): boolean {
  return detectorConstructor() !== null && typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getUserMedia;
}
