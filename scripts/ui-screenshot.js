const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  await page.goto('http://localhost:5171/#/home', { waitUntil: 'networkidle' });
  await page.screenshot({ path: '/tmp/screen-home.png' });
  await page.goto('http://localhost:5171/#/repos', { waitUntil: 'networkidle' });
  await page.screenshot({ path: '/tmp/screen-repos.png' });
  await page.goto('http://localhost:5171/#/search', { waitUntil: 'networkidle' });
  await page.screenshot({ path: '/tmp/screen-search.png' });

  // Inspect computed layout box metrics for the app shell / content.
  await page.goto('http://localhost:5171/#/home', { waitUntil: 'networkidle' });
  const metrics = await page.evaluate(() => {
    function box(sel) {
      const el = document.querySelector(sel);
      if (!el) return null;
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return { sel, left: r.left, right: r.right, width: r.width, padding: cs.padding, margin: cs.margin, maxWidth: cs.maxWidth };
    }
    return {
      html: box('html'),
      body: box('body'),
      appShell: box('.app-shell'),
      sidebar: box('.sidebar'),
      content: box('.content'),
      viewHome: box('#view-home'),
      homeCard: box('.home-card'),
    };
  });
  console.log(JSON.stringify(metrics, null, 2));

  await browser.close();
})();
