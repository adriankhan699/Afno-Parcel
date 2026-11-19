import { useEffect, useRef, type PointerEvent as ReactPointerEvent, type RefObject } from 'react';

interface SignaturePadProps {
  /** The canvas, so the parent can export it as a PNG. */
  canvasRef: RefObject<HTMLCanvasElement | null>;
  /** Called when the pad goes from empty to signed and back. */
  onInkChange: (hasInk: boolean) => void;
  /** Change this number to wipe the pad. */
  clearKey: number;
  invalid?: boolean;
}

const INK = '#0f172a';

function paintBackground(canvas: HTMLCanvasElement) {
  const context = canvas.getContext('2d');
  if (!context) return;
  context.save();
  context.setTransform(1, 0, 0, 1, 0, 0);
  context.fillStyle = '#ffffff';
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.restore();
}

/**
 * A box to sign in with a finger or stylus (pointer events, so it works for touch, pen and mouse; touch-action is none so the
 * page does not scroll while signing). The canvas is drawn at the screen's pixel density so lines stay sharp.
 */
export default function SignaturePad({ canvasRef, onInkChange, clearKey, invalid = false }: SignaturePadProps) {
  const drawing = useRef(false);
  const inked = useRef(false);

  // Size the drawing surface to the box on screen, and start with white paper (so the PNG is not transparent).
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ratio = window.devicePixelRatio || 1;
    const rect = canvas.getBoundingClientRect();
    canvas.width = Math.max(1, Math.round(rect.width * ratio));
    canvas.height = Math.max(1, Math.round(rect.height * ratio));
    const context = canvas.getContext('2d');
    if (context) {
      context.scale(ratio, ratio);
      context.lineWidth = 2.5;
      context.lineCap = 'round';
      context.lineJoin = 'round';
      context.strokeStyle = INK;
    }
    paintBackground(canvas);
  }, [canvasRef]);

  // Wipe on request.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || clearKey === 0) return;
    paintBackground(canvas);
    inked.current = false;
    onInkChange(false);
  }, [clearKey, canvasRef, onInkChange]);

  function point(event: ReactPointerEvent<HTMLCanvasElement>) {
    const rect = event.currentTarget.getBoundingClientRect();
    return { x: event.clientX - rect.left, y: event.clientY - rect.top };
  }

  function handleDown(event: ReactPointerEvent<HTMLCanvasElement>) {
    const context = event.currentTarget.getContext('2d');
    if (!context) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    drawing.current = true;
    const { x, y } = point(event);
    context.beginPath();
    context.moveTo(x, y);
    // A tap leaves a dot.
    context.lineTo(x + 0.01, y + 0.01);
    context.stroke();
    if (!inked.current) {
      inked.current = true;
      onInkChange(true);
    }
  }

  function handleMove(event: ReactPointerEvent<HTMLCanvasElement>) {
    if (!drawing.current) return;
    const context = event.currentTarget.getContext('2d');
    if (!context) return;
    const { x, y } = point(event);
    context.lineTo(x, y);
    context.stroke();
  }

  function handleUp(event: ReactPointerEvent<HTMLCanvasElement>) {
    drawing.current = false;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
  }

  return (
    <canvas
      ref={canvasRef}
      role="img"
      aria-label="Signature box. Draw the receiver's signature here with a finger."
      aria-invalid={invalid || undefined}
      onPointerDown={handleDown}
      onPointerMove={handleMove}
      onPointerUp={handleUp}
      onPointerCancel={handleUp}
      style={{ touchAction: 'none' }}
      className={`h-52 w-full rounded-xl border-2 border-dashed bg-white ${invalid ? 'border-red-400' : 'border-slate-300'}`}
    />
  );
}
