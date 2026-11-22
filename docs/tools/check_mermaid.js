// Usage: MERMAID_JS=<path to mermaid@11.4.1 dist/mermaid.min.js> NODE_PATH=$(npm root -g) node docs/tools/check_mermaid.js <markdown files...>
// Requires Playwright (preinstalled or npm i playwright). Renders each mermaid block in headless Chromium; exits 1 on any failure.
// Renders every ```mermaid block in the given markdown files with Mermaid in headless Chromium.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
(async () => {
  const files = process.argv.slice(2);
  const blocks = [];
  for (const f of files) {
    const txt = fs.readFileSync(f, 'utf8');
    const re = /```mermaid\n([\s\S]*?)```/g; let m; let i = 0;
    while ((m = re.exec(txt))) { i++; const line = txt.slice(0, m.index).split('\n').length; blocks.push({ f, i, line, code: m[1] }); }
  }
  const browser = await chromium.launch();
  const page = await browser.newPage();
  await page.setContent('<html><body></body></html>');
  await page.addScriptTag({ path: process.env.MERMAID_JS || path.join(__dirname, 'node_modules/mermaid/dist/mermaid.min.js') });
  await page.evaluate(() => mermaid.initialize({ startOnLoad: false, securityLevel: 'strict' }));
  let fail = 0;
  for (const b of blocks) {
    const res = await page.evaluate(async ({ code, id }) => {
      try { await mermaid.render(id, code); return null; } catch (e) { return String(e && e.message || e); }
    }, { code: b.code, id: 'd' + Math.random().toString(36).slice(2) });
    if (res) { fail++; console.log(`FAIL ${b.f}:${b.line} (#${b.i})\n  ${res.split('\n').slice(0, 4).join('\n  ')}`); }
  }
  console.log(`${blocks.length - fail}/${blocks.length} diagrams rendered OK`);
  await browser.close();
  process.exit(fail ? 1 : 0);
})();
