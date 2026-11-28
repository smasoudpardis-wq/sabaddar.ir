'use client';
import { use, useEffect, useState } from 'react';
import { ReceiptData, ReceiptView } from '../../receipt-view';

/** US-092 / CUS-009: the digital invoice behind a receipt link (its QR). No sign-in; no customer data is shown or kept. */
export default function PublicReceiptPage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = use(params);
  const [receipt, setReceipt] = useState<ReceiptData | null>(null);
  const [missing, setMissing] = useState(false);

  useEffect(() => {
    void (async () => {
      const r = await fetch(`/bff/r/${encodeURIComponent(token)}`, { cache: 'no-store' });
      if (r.ok) setReceipt(await r.json()); else setMissing(true);
    })();
  }, [token]);

  if (missing) return <p role="alert" data-testid="public-receipt-missing">این لینک فاکتور معتبر نیست، منقضی شده یا فروشگاه آن را باطل کرده است.</p>;
  if (!receipt) return <p>در حال بارگذاری…</p>;
  return <ReceiptView r={receipt} wide />;
}
