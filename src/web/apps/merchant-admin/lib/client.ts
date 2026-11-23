'use client';
// Browser helpers shared by pages (no secrets here: tokens never reach the browser).
export const csrf = () =>
  document.cookie.split('; ').find((c) => c.startsWith('rc_csrf=') || c.startsWith('__Host-rc_csrf='))?.split('=')[1] ?? '';

/** The problem code; for entitlement-exceeded (TEN-003) also the resource, e.g. `entitlement-exceeded:branches` (D09). */
export const codeOf = async (r: Response) => {
  const p = (await r.json().catch(() => null)) as { code?: string; resource?: string } | null;
  if (p?.code === 'entitlement-exceeded' && p.resource) return `entitlement-exceeded:${p.resource}`;
  return p?.code ?? `http-${r.status}`;
};

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
  'duplicate-code': 'این کد قبلاً استفاده شده است.',
  'rate-limited': 'تلاش‌های زیاد. چند دقیقه بعد دوباره امتحان کنید.',
  'self-approval': 'درخواست خودتان را نمی‌توانید تأیید کنید.',
  'pin-invalid': 'PIN اشتباه است.',
  'pin-locked': 'PIN پس از ۵ خطا قفل شد. دوباره کامل وارد شوید.',
  'pin-not-set': 'هنوز PIN تعیین نکرده‌اید.',
  // D09 §10.1: «سقف پلن شما ({مورد}) پر شده است.» — action: upgrade the plan
  'entitlement-exceeded': 'سقف پلن شما پر شده است. برای افزودن بیشتر، پلن را ارتقا دهید.',
  // D09 §10.1: «حساب فروشگاه در حالت فقط‌خواندنی است.» — action: contact support or renew
  'tenant-read-only': 'حساب فروشگاه در حالت فقط‌خواندنی است. برای تمدید یا رفع تعلیق با پشتیبانی تماس بگیرید.',
  'entitlement-exceeded:branches': 'سقف پلن شما (شعبه) پر شده است. برای افزودن بیشتر، پلن را ارتقا دهید.',
  'entitlement-exceeded:registers': 'سقف پلن شما (صندوق) پر شده است. برای افزودن بیشتر، پلن را ارتقا دهید.',
  'entitlement-exceeded:users': 'سقف پلن شما (کاربر) پر شده است. برای افزودن بیشتر، پلن را ارتقا دهید.',
  'reauth-required': 'برای تعیین PIN دوباره وارد شوید.',
  'session-locked': 'صندوق قفل است.',
  'approval-expired': 'مهلت این درخواست تمام شده است.',
  'approval-not-pending': 'این درخواست قبلاً بررسی شده است.',
};

export const ROLE_LABELS: Record<string, string> = {
  TenantOwner: 'مالک', TenantAdmin: 'مدیر فروشگاه', BranchManager: 'مدیر شعبه', Cashier: 'صندوق‌دار',
  WarehouseClerk: 'انباردار', PurchasingOfficer: 'کارشناس خرید', Accountant: 'حسابدار', Auditor: 'حسابرس',
  ReadOnlyAnalyst: 'تحلیل‌گر (فقط خواندنی)',
};
