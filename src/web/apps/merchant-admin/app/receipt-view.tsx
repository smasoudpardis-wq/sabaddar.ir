'use client';
// US-092 / SAL-012 / BR-086: one receipt as printed or opened from its link. React escapes every text (SEC-T-002); the
// legal text and the locked fields come from the industry template version cited by the sale.

export type ReceiptData = {
  storeName: string | null; branchName: string | null; invoiceNo: string; finalizedAt: string;
  lines: { name: string; qty: string; unit: string; unitPrice: string; discount: string; tax: string; lineTotal: string }[];
  totals: { gross: string; discount: string; net: string; tax: string; total: string };
  tenders: { method: string; amount: string }[];
  legalText: string | null; templateVersion: string | null; footer: string | null; signature: string | null;
};

const rial = (v: string) => Number(v).toLocaleString('fa-IR');
const METHODS: Record<string, string> = { Cash: 'نقد', ExternalCardTerminal: 'کارتخوان', OnlineGateway: 'درگاه', CustomerCredit: 'نسیه' };

export function ReceiptView({ r, copy, receiptUrl, wide }: { r: ReceiptData; copy?: number; receiptUrl?: string; wide?: boolean }) {
  return (
    <article data-testid="receipt" data-invoice={r.invoiceNo} style={{ width: wide ? '190mm' : '72mm', margin: '0 auto', fontSize: wide ? 13 : 11 }}>
      {copy !== undefined && copy > 1 && <p data-testid="receipt-copy" style={{ textAlign: 'center', fontWeight: 'bold' }}>رونوشت (نسخه {copy.toLocaleString('fa-IR')})</p>}
      <h1 style={{ fontSize: wide ? 18 : 14, textAlign: 'center', margin: 4 }}>{r.storeName}</h1>
      {r.branchName && <p style={{ textAlign: 'center', margin: 2 }}>شعبه {r.branchName}</p>}
      <p style={{ margin: 2 }}>شماره فاکتور: <span dir="ltr">{r.invoiceNo}</span></p>
      <p style={{ margin: 2 }}>تاریخ: {new Date(r.finalizedAt).toLocaleString('fa-IR')}</p>
      <table style={{ width: '100%', borderCollapse: 'collapse' }}>
        <thead><tr><th style={{ textAlign: 'right' }}>کالا</th><th>مقدار</th><th>فی</th><th>جمع</th></tr></thead>
        <tbody>
          {r.lines.map((l, i) => (
            <tr key={i}><td>{l.name}</td><td>{Number(l.qty).toLocaleString('fa-IR')}</td><td>{rial(l.unitPrice)}</td><td>{rial(l.lineTotal)}</td></tr>
          ))}
        </tbody>
      </table>
      <p style={{ margin: 2 }}>جمع: {rial(r.totals.gross)} · تخفیف: {rial(r.totals.discount)} · مالیات: {rial(r.totals.tax)}</p>
      <p data-testid="receipt-total" style={{ margin: 2, fontWeight: 'bold' }}>قابل پرداخت: {rial(r.totals.total)} ریال</p>
      {r.tenders.map((t, i) => <p key={i} style={{ margin: 2 }}>{METHODS[t.method] ?? t.method}: {rial(t.amount)}</p>)}
      {r.legalText && <p data-testid="receipt-legal" style={{ borderTop: '1px dashed #000', paddingTop: 4 }}>{r.legalText}</p>}
      {r.footer && <p data-testid="receipt-footer">{r.footer}</p>}
      {r.signature && <p>— {r.signature}</p>}
      {receiptUrl && (
        <div data-testid="receipt-link" style={{ borderTop: '1px dashed #000', paddingTop: 4, textAlign: 'center' }}>
          <p style={{ margin: 2 }}>فاکتور دیجیتال:</p>
          <p dir="ltr" style={{ margin: 2, wordBreak: 'break-all', fontSize: 9 }}>{receiptUrl}</p>
        </div>
      )}
      {r.templateVersion && <p style={{ fontSize: 8, color: '#555', textAlign: 'center' }}>قالب {r.templateVersion}</p>}
    </article>
  );
}
