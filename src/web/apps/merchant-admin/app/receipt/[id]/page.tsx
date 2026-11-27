'use client';
import { use, useEffect, useState } from 'react';
import { codeOf, csrf, MESSAGES } from '@/lib/client';
import { ReceiptData, ReceiptView } from '../../receipt-view';

type Printed = { receipt: ReceiptData; format: '80mm' | 'A4'; copy: number; isCopy: boolean; receiptUrl: string };

/**
 * US-092 / SAL-012: the print of a finalized invoice, 80 mm (thermal roll) or A4, through the browser's print. Opening this
 * page counts a print: the first is the original, every later one says it is a copy.
 */
export default function PrintPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ format?: string }> }) {
  const { id } = use(params);
  const { format } = use(searchParams);
  const [printed, setPrinted] = useState<Printed | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    void (async () => {
      const r = await fetch(`/bff/sales/${id}/print`, {
        method: 'POST', headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
        body: JSON.stringify({ format: format === 'A4' ? 'A4' : '80mm' }),
      });
      if (!r.ok) return setError(await codeOf(r));
      setPrinted(await r.json());
    })();
  }, [id, format]);

  if (error) return <p role="alert" data-testid="print-error">{error === 'sale-not-finalized' ? 'فاکتور هنوز صادر نشده است.' : MESSAGES[error] ?? error}</p>;
  if (!printed) return <p>در حال آماده‌سازی…</p>;
  const wide = printed.format === 'A4';
  return (
    <section>
      <style>{`@page { size: ${wide ? 'A4' : '80mm auto'}; margin: ${wide ? '12mm' : '3mm'}; } @media print { .no-print { display: none; } }`}</style>
      <p className="no-print">
        <button type="button" style={{ minHeight: 44 }} onClick={() => window.print()} data-testid="print-now">چاپ</button>{' '}
        <a href="/pos">بازگشت به صندوق</a>
      </p>
      <ReceiptView r={printed.receipt} copy={printed.copy} receiptUrl={printed.receiptUrl} wide={wide} />
    </section>
  );
}
