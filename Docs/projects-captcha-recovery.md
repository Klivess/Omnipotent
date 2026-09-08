# Project CAPTCHA recovery

Projects now default to a **free, keyless browser extension**, rather than requiring a funded token-solving account. The extension operates in the agent's existing Chromium desktop; cookies and login state stay in that browser.

## What is free

The desktop image bundles NopeCHA's automation extension, version **0.6.1**. No account, API key, subscription or credit purchase is configured. Upstream publishes a free allowance of up to **100 recognitions per day**, counted by public IP, and excludes non-residential IPs. Several Project desktops on the same connection share that allowance. A single CAPTCHA may require several recognitions.

This is a practical free option, **not unlimited or guaranteed access**. It sends challenge data to NopeCHA's hosted recognition service; it is not an offline model. The current extension is closed source, despite its public release repository. Paid credits, proxy rotation and account creation are not part of the default workflow.

Primary references, checked 2026-09-08:

- [NopeCHA free service and eligibility](https://developers.nopecha.com/)
- [Keyless extension and IP-based allowance](https://github.com/NopeCHALLC/nopecha-extension#Getting-Started)
- [Automation build configuration](https://developers.nopecha.com/guides/extension_advanced/)
- [Pinned 0.6.1 release](https://github.com/NopeCHALLC/nopecha-extension/releases/tag/0.6.1)

## Agent behavior

1. Inspect the current page. If a challenge response already exists, continue to the intended form and verify its result.
2. Call the existing browser operation with **op: solve_challenge**. The free extension handles supported challenges while the helper observes the same tab for up to 90 seconds by default (configurable from 1 to 120 seconds).
3. Report a response as **present, acceptance unverified**. A CAPTCHA response alone is not evidence of a completed signup, upload or external action.
4. If unresolved, briefly cool down the unchanged document instead of repeating long waits. Use an official API or another supported route where possible. For an essential page, use the existing human takeover and resume the same desktop.

The extension's own recognition runs in the browser. The bounded timeout and cooldown govern the agent's tool calls; they do not promise to cancel the extension's internal jobs.

The commander and worker prompts no longer ask agents to invent solver credentials or register paid accounts. Missing extensions, unsupported challenges, site refusal, quota and connection eligibility are surfaced as actionable limits.

## Deployment

Deploy the updated Omnipotent build and its copied desktop build-context files through the normal release process. The desktop image stamp is now **v11**. The context hash includes the changed Dockerfile, so the existing image build path detects the update.

The Docker build downloads only the pinned official automation archive and verifies SHA-256:

    92ebe154bd34433b4a36e6b2df33006fa6acb19a1b81031a0acb5b72be5e025a

It installs the extension at /usr/local/share/klive-nopecha. Chromium loads it alongside the existing persona extension, including when persona normalization is disabled. No code is downloaded during browser startup.

After the image rebuild, the normal desktop staleness check replaces old containers when they are next provisioned. Browser profiles and cookies persist under the existing project bind mount. Open tabs and other ephemeral container state can be interrupted by replacement; use the existing deployment workflow.

Verify an active desktop reports image v11 and contains the extension. An already running v10 container will not gain the extension merely because the C# process was rebuilt. This task did not deploy or recreate live containers; Docker is not available in the development shell.

Pinned extension releases do not automatically update. When upstream changes CAPTCHA support, update the version and digest together, repeat the browser smoke test, then rebuild the desktop image.

## Optional existing paid integration

The existing paid integration remains available only when the host operator explicitly sets **PROJECTS_CAPTCHA_ALLOW_PAID=1**. Leave that variable unset for free mode. Stored paid credentials alone do not enable fallback, and free-mode tests assert the account registry is not queried.

That optional path now uses provider fallback, bounded timeouts, credential cooldowns and correct provider-specific parameters. Token application is tied to the original document, tab and widget, and only registered success callbacks are invoked. It never reports form acceptance merely because it injected a token.

## Verification

- C# tests cover free-mode credential isolation, terminal success versus semantic success, cooldown across short-lived adapters, already-answered widgets and paid-provider protocol recovery.
- Omnipotent.Tests/Projects/browser-challenge.test.cjs exercises the actual helper JavaScript in Chromium with local HTML fixtures. It covers metadata, multiple forms, callback filtering, stale documents/widgets, and already-answered challenges.
- The official NopeCHA Turnstile demo produced a browser response in an isolated Chromium test with version 0.6.1 loaded alongside the persona extension and with no account key. This confirms basic compatibility; it does not establish a general solve rate or prove server-side acceptance.

Run C# checks with the repository's .NET runtime. The development machine has .NET 8 and 10 but no .NET 9 runtime, so verification here used **DOTNET_ROLL_FORWARD=Major** to run the net9.0 tests on .NET 10.

For browser fixtures, install Playwright in a separate test-tools directory, install its Chromium browser, set NODE_PATH to that directory's node_modules, and run:

    node Omnipotent.Tests/Projects/browser-challenge.test.cjs

Fixtures make no solver-provider requests and consume no credits.
