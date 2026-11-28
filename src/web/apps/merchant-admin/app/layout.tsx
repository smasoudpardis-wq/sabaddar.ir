import type { ReactNode } from 'react';
import { headers } from 'next/headers';
import './globals.css';

export const metadata = { title: 'RetailCloud — مدیریت فروشگاه' };
export const dynamic = 'force-dynamic';

export default async function RootLayout({ children }: { children: ReactNode }) {
  await headers(); // dynamic rendering so Next applies the CSP nonce to its scripts
  return (
    <html lang="fa" dir="rtl">
      <body style={{ fontFamily: 'Tahoma, Vazirmatn, sans-serif', background: '#F7F8FA', color: '#14171C', margin: 0 }}>
        <a className="skip-link" href="#main">رفتن به محتوای صفحه</a>
        <main id="main" tabIndex={-1} style={{ maxWidth: 640, margin: '0 auto', padding: 16 }}>{children}</main>
      </body>
    </html>
  );
}
