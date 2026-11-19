import { useEffect, useRef, type ReactNode } from 'react';

interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: string;
  /** Wider for forms with many fields. */
  wide?: boolean;
  /** Only needed when two dialogs can be open at once (the second one needs its own id). */
  titleId?: string;
  children: ReactNode;
}

/** A modal built on the native <dialog>: focus is trapped and Escape closes it. Children are only rendered while open, so forms start fresh. */
export default function Dialog({ open, onClose, title, wide = false, titleId = 'dialog-title', children }: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      aria-labelledby={titleId}
      onClose={onClose}
      className={`w-full rounded-xl p-0 shadow-xl backdrop:bg-black/40 ${wide ? 'max-w-2xl' : 'max-w-md'}`}
    >
      {open && (
        <div className="p-6">
          <h2 id={titleId} className="text-lg font-semibold text-slate-900">
            {title}
          </h2>
          <div className="mt-4">{children}</div>
        </div>
      )}
    </dialog>
  );
}
