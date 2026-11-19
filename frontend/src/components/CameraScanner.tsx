import { useEffect, useRef, useState } from 'react';
import { detectorConstructor, type DetectorConstructor } from '../lib/camera';
import { CAMERA_FORMATS } from '../lib/scan';

interface CameraScannerProps {
  /** Called with the raw text of each code read. The same code is not reported again for a few seconds. */
  onCode: (code: string) => void;
  onClose: () => void;
}

const REPEAT_QUIET_MS = 3000;
const POLL_MS = 250;

/** Live camera view that reads QR codes and Code 128 barcodes. Stops the camera when closed or when the page is left. */
export default function CameraScanner({ onCode, onClose }: CameraScannerProps) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const onCodeRef = useRef(onCode);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    onCodeRef.current = onCode;
  }, [onCode]);

  useEffect(() => {
    const Found = detectorConstructor();
    if (!Found) {
      return;
    }
    const Detector: DetectorConstructor = Found;

    let stream: MediaStream | null = null;
    let timer: number | undefined;
    let cancelled = false;
    const seen = new Map<string, number>();

    async function start() {
      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
        if (cancelled) {
          stream.getTracks().forEach((t) => t.stop());
          return;
        }
        const video = videoRef.current;
        if (!video) return;
        video.srcObject = stream;
        await video.play();

        const detector = new Detector({ formats: [...CAMERA_FORMATS] });
        timer = window.setInterval(() => {
          if (video.readyState < 2) return;
          detector
            .detect(video)
            .then((codes) => {
              const now = Date.now();
              for (const code of codes) {
                const last = seen.get(code.rawValue);
                if (last === undefined || now - last > REPEAT_QUIET_MS) {
                  seen.set(code.rawValue, now);
                  onCodeRef.current(code.rawValue);
                }
              }
            })
            .catch(() => {
              // a frame that cannot be read is simply skipped
            });
        }, POLL_MS);
      } catch {
        setError('The camera could not be opened. Allow camera access in the browser, or type the code instead.');
      }
    }

    void start();
    return () => {
      cancelled = true;
      if (timer !== undefined) window.clearInterval(timer);
      stream?.getTracks().forEach((t) => t.stop());
    };
  }, []);

  return (
    <div className="rounded-xl border border-slate-200 bg-slate-900 p-3 text-white">
      <div className="flex items-center justify-between gap-3 pb-2 text-sm">
        <span>Point the camera at the barcode or QR code on the label.</span>
        <button type="button" onClick={onClose} className="rounded-md bg-white/10 px-3 py-1 text-xs font-semibold hover:bg-white/20">
          Stop camera
        </button>
      </div>
      {error ? (
        <p role="alert" className="rounded-lg bg-red-900/60 p-3 text-sm">
          {error}
        </p>
      ) : (
        <video ref={videoRef} muted playsInline aria-label="Camera view" className="max-h-72 w-full rounded-lg bg-black object-cover" />
      )}
    </div>
  );
}
