// Run with Node and Playwright installed (NODE_PATH may point to a separate test tool directory).
// Uses local HTML fixtures only: no CAPTCHA-provider requests or solver credits.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const helper = fs.readFileSync(path.join(__dirname,
  '../../Omnipotent/Services/Projects/Containers/browser-inspect.py'), 'utf8');
const script = name => helper.match(new RegExp(name + ' = r"""\\r?\\n([\\s\\S]*?)"""'))[1];
const probeJs = script('CHALLENGE_PROBE_JS');
const injectJs = script('CHALLENGE_INJECT_JS');
const expected = (probe, widget = probe.widgets[0]) => ({
  documentId: probe.documentId, widgetId: widget.id, provider: widget.provider, sitekey: widget.sitekey
});
const inject = (page, data) => {
  let i = 0;
  const values = ['TEST_RESPONSE', data];
  return page.evaluate(injectJs.replace(/%s/g, () => JSON.stringify(values[i++])));
};

(async () => {
  const browser = await chromium.launch({ headless: true });
  let count = 0;
  async function check(name, html, run) {
    const page = await browser.newPage();
    await page.route('**/*', route => route.abort());
    try {
      await page.setContent(html);
      await run(page);
      console.log('PASS ' + name);
      count++;
    } finally { await page.close(); }
  }
  try {
    await check('iframe metadata does not erase container metadata',
      '<form><div class="g-recaptcha" data-sitekey="K" data-action="signup" data-s="extra"><iframe src="https://google.com/recaptcha/enterprise/anchor?k=K&size=invisible"></iframe></div><textarea name="g-recaptcha-response"></textarea></form>',
      async page => {
        const p = await page.evaluate(probeJs);
        assert.equal(p.widgets.length, 1);
        assert.equal(p.widgets[0].provider, 'recaptcha_enterprise');
        assert.equal(p.widgets[0].action, 'signup');
        assert.equal(p.widgets[0].dataS, 'extra');
        assert.equal(p.widgets[0].invisible, true);
        assert.equal((await inject(page, expected(p))).injected, 1);
        assert.equal((await page.evaluate(probeJs)).widgets[0].responsePresent, true);
      });
    await check('same sitekey in two forms stays isolated',
      '<form id="a"><div class="cf-turnstile" data-sitekey="K"></div><input name="cf-turnstile-response"></form><form id="b"><div class="cf-turnstile" data-sitekey="K"></div><input name="cf-turnstile-response"></form>',
      async page => {
        const p = await page.evaluate(probeJs);
        assert.equal(p.widgets.length, 2);
        await inject(page, expected(p, p.widgets[1]));
        assert.equal(await page.locator('#a input').inputValue(), '');
        assert.equal(await page.locator('#b input').inputValue(), 'TEST_RESPONSE');
      });
    await check('only registered success callbacks run, once',
      '<div class="g-recaptcha" data-sitekey="K" data-callback="app.success"></div>',
      async page => {
        await page.evaluate(() => {
          window.calls = [];
          window.app = { success(t) { window.calls.push(['success', t]); } };
          window.captchaCallback = () => window.calls.push(['guessed']);
          window.___grecaptcha_cfg = { clients: { 1: { sitekey: 'K', callback: window.app.success,
            'error-callback': () => window.calls.push(['error']),
            'expired-callback': () => window.calls.push(['expired']) } } };
        });
        const p = await page.evaluate(probeJs);
        assert.equal((await inject(page, expected(p))).callbacks, 1);
        assert.deepEqual(await page.evaluate(() => window.calls), [['success', 'TEST_RESPONSE']]);
      });
    await check('replaced widget rejects stale response',
      '<form><div class="h-captcha" data-sitekey="K"></div><textarea name="h-captcha-response"></textarea></form>',
      async page => {
        const p = await page.evaluate(probeJs);
        await page.locator('.h-captcha').evaluate(node => node.replaceWith(node.cloneNode(true)));
        assert.equal((await inject(page, expected(p))).error.code, 'stale-widget');
        assert.equal(await page.locator('textarea').inputValue(), '');
      });
    await check('wrong document cannot receive response',
      '<form><div class="h-captcha" data-sitekey="K"></div><textarea name="h-captcha-response"></textarea></form>',
      async page => {
        const p = await page.evaluate(probeJs);
        assert.equal((await inject(page, {...expected(p), documentId: 'other'})).error.code, 'stale-document');
        assert.equal(await page.locator('textarea').inputValue(), '');
      });
    await check('no synthetic field is created in an unrelated form',
      '<form id="unrelated"><input name="email"></form><div class="cf-turnstile" data-sitekey="K"></div>',
      async page => {
        const p = await page.evaluate(probeJs);
        assert.equal((await inject(page, expected(p))).error.code, 'inject-nowhere');
        assert.equal(await page.locator('textarea').count(), 0);
      });
    await check('hcaptcha hash parameters are detected',
      '<iframe src="https://newassets.hcaptcha.com/captcha/v1/example/static/hcaptcha.html#sitekey=H"></iframe>',
      async page => {
        const p = await page.evaluate(probeJs);
        assert.equal(p.widgets[0].sitekey, 'H');
        assert.equal(p.widgets[0].provider, 'hcaptcha');
      });
    await check('answered widgets do not request another token',
      '<form><div class="cf-turnstile" data-sitekey="K"></div><input name="cf-turnstile-response" value="existing"></form>',
      async page => {
        const p = await page.evaluate(probeJs);
        assert.equal(p.detected, false);
        assert.equal(p.widgets[0].responsePresent, true);
      });
    await check('unrelated sitekey is not a CAPTCHA',
      '<div data-sitekey="analytics">Ordinary page</div>',
      async page => assert.equal((await page.evaluate(probeJs)).detected, false));
    await check('interstitial without sitekey remains a visible blocker',
      '<title>Just a moment...</title><p>Checking your browser</p>',
      async page => {
        const p = await page.evaluate(probeJs);
        assert.equal(p.interstitial, true);
        assert.equal(p.detected, true);
        assert.equal(p.widgets.length, 0);
      });
    console.log(count + ' browser fixtures passed.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
