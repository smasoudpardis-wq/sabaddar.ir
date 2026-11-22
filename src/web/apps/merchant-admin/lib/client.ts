'use client';
// Browser helpers shared by pages (no secrets here: tokens never reach the browser).
export const csrf = () =>
  document.cookie.split('; ').find((c) => c.startsWith('rc_csrf=') || c.startsWith('__Host-rc_csrf='))?.split('=')[1] ?? '';

export const codeOf = async (r: Response) =>
  ((await r.json().catch(() => null)) as { code?: string } | null)?.code ?? `http-${r.status}`;

export const postJson = (url: string, body?: unknown) =>
  fetch(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'x-csrf-token': csrf() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

/** D09 §10.1 messages used outside the home page. */
export const MESSAGES: Record<string, string> = {
  forbidden: 'نقش شما اجازه این کار را نمی‌دهد.',
  'not-found': 'این دعوت معتبر نیست، قبلاً استفاده شده یا منقضی شده است.',
  validation: 'اطلاعات واردشده معتبر نیست.',
  'duplicate-assignment': 'این کاربر همین نقش را در همین محدوده دارد.',
  'last-owner': 'فروشگاه باید دست‌کم یک مالک داشته باشد.',
  'tenant-not-selected': 'ابتدا فروشگاه را انتخاب کنید.',
};

export const ROLE_LABELS: Record<string, string> = {
  TenantOwner: 'مالک', TenantAdmin: 'مدیر فروشگاه', BranchManager: 'مدیر شعبه', Cashier: 'صندوق‌دار',
  WarehouseClerk: 'انباردار', PurchasingOfficer: 'کارشناس خرید', Accountant: 'حسابدار', Auditor: 'حسابرس',
  ReadOnlyAnalyst: 'تحلیل‌گر (فقط خواندنی)',
};
