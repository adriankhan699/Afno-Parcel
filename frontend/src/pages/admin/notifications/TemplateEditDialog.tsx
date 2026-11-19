import { useEffect, useRef, useState } from 'react';
import Dialog from '../../../components/Dialog';
import { Alert, PrimaryButton, SecondaryButton, TextField } from '../../../components/form';
import ToggleRow from '../../../components/ToggleRow';
import { api } from '../../../lib/apiClient';
import {
  canSaveTemplate,
  channelLabel,
  insertAtSelection,
  placeholderToken,
  smsHint,
  templateKeyLabel,
  usedPlaceholders,
} from '../../../lib/notification';
import { notificationApi } from '../../../lib/notificationApi';
import { toNotificationError, useNotificationAction } from '../../../lib/useNotificationAction';
import type {
  NotificationTemplateResponse,
  PreviewTemplateRequest,
  PreviewTemplateResponse,
  UpdateNotificationTemplateRequest,
} from '../../../types/api';

interface TemplateEditDialogProps {
  /** The template to edit; null = closed. */
  template: NotificationTemplateResponse | null;
  /** The placeholder names this template may use. */
  allowedPlaceholders: readonly string[];
  onClose: () => void;
  onSaved: () => void;
}

export default function TemplateEditDialog({ template, allowedPlaceholders, onClose, onSaved }: TemplateEditDialogProps) {
  return (
    <Dialog
      open={template !== null}
      onClose={onClose}
      wide
      title={template ? `Edit ${templateKeyLabel(template.key)} (${channelLabel(template.channel)})` : 'Edit template'}
    >
      {template && <EditBody key={template.id} template={template} allowedPlaceholders={allowedPlaceholders} onClose={onClose} onSaved={onSaved} />}
    </Dialog>
  );
}

interface PreviewState {
  /** The text that was checked. */
  body: string;
  result?: PreviewTemplateResponse;
  failed?: string;
}

const PREVIEW_DELAY_MS = 400;

function EditBody({
  template,
  allowedPlaceholders,
  onClose,
  onSaved,
}: {
  template: NotificationTemplateResponse;
  allowedPlaceholders: readonly string[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const action = useNotificationAction();
  const textarea = useRef<HTMLTextAreaElement>(null);
  const [body, setBody] = useState(template.body);
  const [providerName, setProviderName] = useState(template.providerTemplateName ?? '');
  const [isActive, setIsActive] = useState(template.isActive);
  const isWhatsApp = template.channel === 'WhatsApp';

  // The preview is asked for shortly after the typing stops, and every answer is remembered with the text it was for.
  const [debouncedBody, setDebouncedBody] = useState(template.body);
  const [preview, setPreview] = useState<PreviewState | null>(null);

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedBody(body), PREVIEW_DELAY_MS);
    return () => clearTimeout(timer);
  }, [body]);

  useEffect(() => {
    if (!debouncedBody.trim()) return;
    const controller = new AbortController();
    const request: PreviewTemplateRequest = { key: template.key, body: debouncedBody };
    api
      .post<PreviewTemplateResponse>(notificationApi.templatePreview, request, controller.signal)
      .then((result) => setPreview({ body: debouncedBody, result }))
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        setPreview({ body: debouncedBody, failed: toNotificationError(err) });
      });
    return () => controller.abort();
  }, [template.key, debouncedBody]);

  const hasText = body.trim().length > 0;
  // The check is "running" until an answer exists for exactly the text now in the box.
  const checking = hasText && (body !== debouncedBody || preview?.body !== body);
  const current = preview?.body === body ? preview : null;
  const problems = current?.result?.problems ?? [];
  const canSave = canSaveTemplate({ body, checking, problems });

  const parameters = current?.result?.parameters ?? usedPlaceholders(body);
  const sampleText = current?.result?.text ?? body;

  function insert(name: string) {
    const box = textarea.current;
    const start = box?.selectionStart ?? body.length;
    const end = box?.selectionEnd ?? body.length;
    const next = insertAtSelection(body, start, end, placeholderToken(name));
    setBody(next.text);
    setTimeout(() => {
      box?.focus();
      box?.setSelectionRange(next.cursor, next.cursor);
    }, 0);
  }

  async function handleSave() {
    if (!canSave) return;
    const request: UpdateNotificationTemplateRequest = {
      body,
      providerTemplateName: isWhatsApp ? providerName.trim() || null : (template.providerTemplateName ?? null),
      isActive,
    };
    const done = await action.run(() => api.put<NotificationTemplateResponse>(notificationApi.template(template.id), request));
    if (done.ok) {
      onSaved();
      onClose();
    }
  }

  return (
    <div className="space-y-5">
      <div>
        <label htmlFor="template-body" className="mb-1 block text-sm font-medium text-slate-700">
          Message text
        </label>
        <textarea
          ref={textarea}
          id="template-body"
          rows={5}
          value={body}
          onChange={(event) => setBody(event.target.value)}
          aria-describedby="template-body-help"
          aria-invalid={problems.length > 0 ? true : undefined}
          className={`w-full rounded-lg border bg-white px-3 py-2 text-sm outline-none focus:border-brand-600 focus:ring-2 focus:ring-brand-100 ${problems.length > 0 ? 'border-red-400' : 'border-slate-300'}`}
        />
        <div id="template-body-help" className="mt-1 space-y-1 text-xs text-slate-500">
          <p>
            {body.length} characters in the template. Placeholders such as {'{tracking}'} are replaced with the real values when the message is sent.
          </p>
          {!isWhatsApp && hasText && <p>With sample values: {smsHint(sampleText)}</p>}
        </div>
      </div>

      <div>
        <p className="text-sm font-medium text-slate-700">Placeholders you can use</p>
        <p className="mt-0.5 text-xs text-slate-500">Click one to put it in the text where the cursor is.</p>
        <div className="mt-2 flex flex-wrap gap-2">
          {allowedPlaceholders.length === 0 && <span className="text-xs text-slate-500">This template has no placeholders.</span>}
          {allowedPlaceholders.map((name) => (
            <button
              key={name}
              type="button"
              onClick={() => insert(name)}
              className="rounded-full border border-slate-300 bg-slate-50 px-3 py-1 font-mono text-xs text-slate-800 hover:bg-brand-50 hover:text-brand-700"
            >
              {placeholderToken(name)}
            </button>
          ))}
        </div>
      </div>

      {isWhatsApp && (
        <div className="space-y-2">
          <TextField
            id="template-provider-name"
            label="WhatsApp template name"
            value={providerName}
            onChange={setProviderName}
            maxLength={100}
            placeholder="e.g. parcel_booked"
          />
          <div className="rounded-lg bg-blue-50 p-3 text-xs text-blue-800">
            <p>
              WhatsApp does not send the text above. Meta sends the template it has <strong>approved</strong> under this name, so the name must match an approved template exactly. The text here is only our own copy.
            </p>
            <p className="mt-1">
              Meta fills the numbered fields of its template with the placeholders in the order they <strong>first appear</strong> in the text, so the approved template must have the same fields in the same order.
            </p>
            {parameters.length > 0 && (
              <ol className="mt-2 list-inside list-decimal font-mono">
                {parameters.map((name, index) => (
                  <li key={name}>
                    {`{{${index + 1}}} = ${placeholderToken(name)}`}
                  </li>
                ))}
              </ol>
            )}
          </div>
        </div>
      )}

      <ToggleRow
        id="template-active"
        label="Active"
        checked={isActive}
        onChange={setIsActive}
        description="When switched off, nothing is sent with this template (messages are skipped)."
      />

      <div aria-live="polite" className="space-y-3">
        <div>
          <p className="text-sm font-medium text-slate-700">Preview with sample values</p>
          <div className="mt-1 rounded-lg bg-slate-50 p-3 text-sm text-slate-900">
            {!hasText && <p className="text-slate-500">Write the message text to see a preview.</p>}
            {hasText && !current && !preview && <p className="text-slate-500">Checking…</p>}
            {hasText && (current?.result || (!current && preview?.result)) && (
              <p className={`whitespace-pre-wrap ${checking ? 'opacity-60' : ''}`}>{(current?.result ?? preview?.result)?.text}</p>
            )}
            {hasText && current?.failed && <p className="text-amber-800">The preview could not be made: {current.failed} The server checks the template again when you save.</p>}
          </div>
          {checking && hasText && <p className="mt-1 text-xs text-slate-500">Checking…</p>}
        </div>
        {problems.length > 0 && (
          <Alert>
            <p className="font-semibold">Fix these before saving:</p>
            <ul className="mt-1 list-disc pl-5">
              {problems.map((problem) => (
                <li key={problem}>{problem}</li>
              ))}
            </ul>
          </Alert>
        )}
        {action.error && <Alert>{action.error}</Alert>}
      </div>

      <div className="flex justify-end gap-2">
        <SecondaryButton type="button" onClick={onClose} disabled={action.busy}>
          Cancel
        </SecondaryButton>
        <PrimaryButton type="button" onClick={() => void handleSave()} disabled={!canSave || action.busy}>
          {action.busy ? 'Saving…' : 'Save template'}
        </PrimaryButton>
      </div>
    </div>
  );
}
