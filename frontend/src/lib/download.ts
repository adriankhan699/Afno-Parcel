const BYTE_ORDER_MARK = String.fromCharCode(0xfeff);

/**
 * Saves text as a file in the browser, without sending it anywhere. A byte-order mark is added to CSV files so Excel reads
 * names with Nepali characters correctly.
 */
export function downloadTextFile(fileName: string, text: string, mimeType = 'text/csv;charset=utf-8'): void {
  const body = mimeType.startsWith('text/csv') ? `${BYTE_ORDER_MARK}${text}` : text;
  const url = URL.createObjectURL(new Blob([body], { type: mimeType }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noopener';
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
