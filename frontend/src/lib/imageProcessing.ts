import { JPEG_QUALITY, MAX_IMAGE_EDGE, fitWithin, validatePickedImage, validateProcessedSize } from './delivery';

// Browser-only glue (canvas and files). The decisions it makes live in delivery.ts, where they are unit-tested.

function canvasToBlob(canvas: HTMLCanvasElement, type: string, quality?: number): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('The image could not be prepared.'))), type, quality);
  });
}

export class ImageRejectedError extends Error {}

/**
 * Shrinks a photo from the camera to at most 1600 pixels on its longest side and re-encodes it as a JPEG (quality 0.8), which
 * also drops any location data stored in the original. Throws ImageRejectedError with a message to show the agent.
 */
export async function preparePhoto(file: File): Promise<Blob> {
  const rejected = validatePickedImage(file);
  if (rejected) throw new ImageRejectedError(rejected);

  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  } catch {
    throw new ImageRejectedError('That file could not be read as a photo. Try another one.');
  }

  try {
    const size = fitWithin(bitmap.width, bitmap.height, MAX_IMAGE_EDGE);
    const canvas = document.createElement('canvas');
    canvas.width = size.width;
    canvas.height = size.height;
    const context = canvas.getContext('2d');
    if (!context) throw new ImageRejectedError('This browser cannot prepare photos.');
    context.fillStyle = '#ffffff';
    context.fillRect(0, 0, size.width, size.height);
    context.drawImage(bitmap, 0, 0, size.width, size.height);

    const blob = await canvasToBlob(canvas, 'image/jpeg', JPEG_QUALITY);
    const tooBig = validateProcessedSize(blob.size);
    if (tooBig) throw new ImageRejectedError(tooBig);
    return blob;
  } finally {
    bitmap.close();
  }
}

/** The signature pad's drawing as a PNG. */
export async function signatureToBlob(canvas: HTMLCanvasElement): Promise<Blob> {
  const blob = await canvasToBlob(canvas, 'image/png');
  const tooBig = validateProcessedSize(blob.size);
  if (tooBig) throw new ImageRejectedError(tooBig);
  return blob;
}
