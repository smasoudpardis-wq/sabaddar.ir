// US-185 / NFR-UX-001..005: dependency-free accessibility rules (a subset of WCAG 2.2 A/AA that can be decided from the DOM).
// Runs inside the page; returns [{rule, detail}]. A full axe-core run in CI and a Screen Reader review stay on the US-185 list.
export const auditInPage = () => {
  const out = [];
  const add = (rule, el, extra = '') => out.push({ rule, detail: `${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''}${el.getAttribute('name') ? `[name=${el.getAttribute('name')}]` : ''}${el.getAttribute('data-testid') ? `[${el.getAttribute('data-testid')}]` : ''} ${extra}`.trim() });
  const visible = (el) => {
    const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
  };
  const text = (el) => (el.innerText || el.textContent || '').trim();
  const labelledBy = (el) => (el.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean).map((i) => document.getElementById(i)?.textContent?.trim() ?? '').join(' ').trim();
  const name = (el) => el.getAttribute('aria-label')?.trim() || labelledBy(el) || el.getAttribute('title')?.trim() || '';

  const html = document.documentElement;
  if (!html.lang) out.push({ rule: 'html-lang', detail: 'missing lang' });
  if (html.dir !== 'rtl') out.push({ rule: 'html-dir', detail: `dir=${html.dir}` });
  if (!document.title.trim()) out.push({ rule: 'document-title', detail: 'empty' });
  if (!document.querySelector('main, [role=main]')) out.push({ rule: 'landmark-main', detail: 'no main' });
  const h1 = [...document.querySelectorAll('h1')].filter(visible);
  if (h1.length !== 1) out.push({ rule: 'single-h1', detail: `${h1.length} h1` });
  let last = 0;
  for (const h of [...document.querySelectorAll('h1,h2,h3,h4,h5,h6')].filter(visible)) {
    const lvl = Number(h.tagName[1]);
    if (last && lvl > last + 1) add('heading-order', h, `h${last}→h${lvl}`);
    last = lvl;
  }

  for (const el of document.querySelectorAll('input, select, textarea')) {
    const t = (el.getAttribute('type') || '').toLowerCase();
    if (t === 'hidden' || !visible(el)) continue;
    const labelled = (el.labels && el.labels.length > 0 && [...el.labels].some((l) => text(l))) || name(el);
    if (!labelled) add('form-label', el);
  }
  for (const el of document.querySelectorAll('button, [role=button]')) {
    if (visible(el) && !text(el) && !name(el)) add('button-name', el);
  }
  for (const el of document.querySelectorAll('a[href]')) {
    if (visible(el) && !text(el) && !name(el)) add('link-name', el);
  }
  for (const el of document.querySelectorAll('img')) {
    if (!el.hasAttribute('alt')) add('image-alt', el);
  }
  for (const el of document.querySelectorAll('table')) {
    if (visible(el) && el.querySelectorAll('td').length > 0 && el.querySelectorAll('th').length === 0) add('table-headers', el);
  }
  const ids = new Map();
  for (const el of document.querySelectorAll('[id]')) ids.set(el.id, (ids.get(el.id) ?? 0) + 1);
  for (const [id, n] of ids) if (n > 1) out.push({ rule: 'duplicate-id', detail: `#${id} ×${n}` });
  for (const el of document.querySelectorAll('[tabindex]')) {
    if (Number(el.getAttribute('tabindex')) > 0) add('tabindex-positive', el);
  }
  // WCAG 2.2 · 2.5.8 target size (minimum): 24 × 24 CSS px for controls on their own (not inline in a sentence)
  for (const el of document.querySelectorAll('button, input:not([type=hidden]):not([type=checkbox]):not([type=radio]), select, [role=button]')) {
    if (!visible(el)) continue;
    const r = el.getBoundingClientRect();
    if (r.width < 24 || r.height < 24) add('target-size', el, `${Math.round(r.width)}×${Math.round(r.height)}`);
  }
  // messages that appear after an action must be announced: an element showing an error needs role=alert or status
  for (const el of document.querySelectorAll('[data-testid$="error"], [data-testid$="-msg"]')) {
    if (visible(el) && !['alert', 'status'].includes(el.getAttribute('role') ?? '') && !el.closest('[role=alert],[role=status],[aria-live]')) add('live-region', el);
  }
  return out;
};
