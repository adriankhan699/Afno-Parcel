import { useState } from 'react';
import { Alert } from '../../../components/form';
import { TableShell, Th, theadClass } from '../../../components/NetworkBits';
import { formatNepalDateTime } from '../../../lib/format';
import { groupTemplates, placeholdersFor, templateKeyLabel } from '../../../lib/notification';
import { notificationApi } from '../../../lib/notificationApi';
import { useApi } from '../../../lib/useApi';
import type { NotificationTemplateResponse, NotificationTemplatesResponse } from '../../../types/api';
import TemplateEditDialog from './TemplateEditDialog';

function TemplateCell({ template, canEdit, onEdit }: { template?: NotificationTemplateResponse; canEdit: boolean; onEdit: (t: NotificationTemplateResponse) => void }) {
  if (!template) return <span className="text-slate-400">No template</span>;
  return (
    <div className="max-w-sm space-y-1">
      <div className="flex flex-wrap items-center gap-2">
        <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${template.isActive ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-200 text-slate-600'}`}>
          {template.isActive ? 'Active' : 'Off'}
        </span>
        <span className="text-xs text-slate-500">{template.length} characters</span>
        {canEdit && (
          <button
            type="button"
            onClick={() => onEdit(template)}
            aria-label={`Edit ${template.key} ${template.channel} template`}
            className="rounded-md border border-slate-300 bg-white px-3 py-1 text-xs font-semibold text-slate-700 hover:bg-slate-50"
          >
            Edit
          </button>
        )}
      </div>
      <p className="whitespace-pre-wrap text-slate-800">{template.body}</p>
      {template.channel === 'WhatsApp' && (
        <p className="text-xs text-slate-500">
          Meta template: <span className="font-mono">{template.providerTemplateName ?? 'not set'}</span>
        </p>
      )}
      {template.parameters.length > 0 && (
        <p className="text-xs text-slate-500">
          Fields in order: <span className="font-mono">{template.parameters.map((p) => `{${p}}`).join(', ')}</span>
        </p>
      )}
      <p className="text-xs text-slate-400">Changed {formatNepalDateTime(template.updatedAtUtc)}</p>
    </div>
  );
}

/** The message texts, one row per template key with the SMS and WhatsApp versions side by side. Only a super admin can edit. */
export default function TemplatesTab({ canEdit }: { canEdit: boolean }) {
  const [reloadKey, setReloadKey] = useState(0);
  const [editing, setEditing] = useState<NotificationTemplateResponse | null>(null);
  const result = useApi<NotificationTemplatesResponse>(notificationApi.templates, reloadKey);

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold">Templates</h2>
        <p className="mt-1 text-sm text-slate-600">
          The text of each message. {canEdit ? 'Choose Edit to change one.' : 'You can read them here; only a super admin can change them.'} For WhatsApp, Meta sends the template it has approved: the text here is only our copy.
        </p>
      </div>
      <div aria-live="polite">
        {(result.status === 'loading' || result.status === 'idle') && <p className="text-sm text-slate-500">Loading templates…</p>}
        {result.status === 'error' && <Alert>{result.message}</Alert>}
        {result.status === 'loaded' && result.data.templates.length === 0 && (
          <p className="rounded-xl border border-dashed border-slate-300 bg-white p-8 text-center text-sm text-slate-600">There are no templates yet.</p>
        )}
        {result.status === 'loaded' && result.data.templates.length > 0 && (
          <>
            <TableShell>
              <caption className="sr-only">Message templates by key, with the SMS and WhatsApp versions</caption>
              <thead className={theadClass}>
                <tr>
                  <Th>Template</Th>
                  <Th>SMS</Th>
                  <Th>WhatsApp</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 align-top">
                {groupTemplates(result.data.templates).map((group) => (
                  <tr key={group.key}>
                    <th scope="row" className="px-4 py-3 text-left font-medium text-slate-900">
                      {templateKeyLabel(group.key)}
                      <span className="block font-mono text-xs font-normal text-slate-500">{group.key}</span>
                    </th>
                    <td className="px-4 py-3">
                      <TemplateCell template={group.sms} canEdit={canEdit} onEdit={setEditing} />
                    </td>
                    <td className="px-4 py-3">
                      <TemplateCell template={group.whatsApp} canEdit={canEdit} onEdit={setEditing} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableShell>
            <TemplateEditDialog
              template={editing}
              allowedPlaceholders={editing ? placeholdersFor(result.data.allowedPlaceholders, editing.key, editing.channel) : []}
              onClose={() => setEditing(null)}
              onSaved={() => setReloadKey((k) => k + 1)}
            />
          </>
        )}
      </div>
    </div>
  );
}
