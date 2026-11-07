const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const net = require('net');
const path = require('path');

/**
 * Invisible browser used to get past Cloudflare's managed challenge and to run
 * JavaScript-gated download pages.
 *
 * A plain `--headless` Chromium (and anything launched with Playwright's automation
 * flags) is fingerprinted and never clears the challenge, so we start the user's own
 * Chrome/Edge with a private profile on a random debugging port, park the window far
 * off-screen, drive it over CDP, and reuse the same instance for every fetch in this
 * run. Nothing is shown to the user and they never see a captcha. The profile is kept
 * between runs so the cf_clearance cookie survives and later fetches resolve instantly.
 */

const CACHE_DIR = path.join(__dirname, '..', '..', 'data', 'cache');
// One private profile per CLI run: a shared profile can be left locked by a crashed run,
// which made downloads hang for two minutes before giving up.
const PROFILE_DIR = path.join(CACHE_DIR, `browser-profile-${process.pid}`);

const BROWSER_CANDIDATES = [
  process.env.BROWSER_PATH,
  process.env.CHROME_PATH,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/google-chrome',
  '/usr/bin/google-chrome-stable',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
  '/usr/bin/microsoft-edge',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
].filter(Boolean);

const CHALLENGE_TEXT = /just a moment|performing security verification|checking your browser|verifying you are human|enable javascript and cookies to continue/i;
const HARD_BLOCK_TEXT = /error\s*10\d\d|you are being rate limited|access denied|attention required/i;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function findBrowser() {
  for (const candidate of BROWSER_CANDIDATES) {
    try {
      if (fs.existsSync(candidate)) return candidate;
    } catch (_) { /* ignore */ }
  }
  return null;
}

function findFreePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

function killTree(pid) {
  if (!pid) return;
  try {
    if (process.platform === 'win32') {
      spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { stdio: 'ignore' });
    } else {
      try { process.kill(-pid, 'SIGKILL'); } catch (_) { process.kill(pid, 'SIGKILL'); }
    }
  } catch (_) { /* ignore */ }
}

/** Kills browsers a previous (crashed) run left holding our private profile. */
function killStaleProfileBrowsers(profileDir) {
  if (process.platform !== 'win32') return;
  try {
    const escaped = profileDir.replace(/\\/g, '\\\\');
    const script =
      'Get-CimInstance Win32_Process -Filter "Name=\'chrome.exe\' or Name=\'msedge.exe\'" | ' +
      `Where-Object { $_.CommandLine -like '*${escaped}*' } | ` +
      'ForEach-Object { $_.ProcessId }';
    const out = spawnSync('powershell', ['-NoProfile', '-Command', script], { encoding: 'utf8', timeout: 20000 });
    for (const pid of String(out.stdout || '').split(/\s+/).filter(Boolean)) {
      killTree(Number(pid));
    }
  } catch (_) { /* ignore */ }
}

async function waitForDebugEndpoint(port, timeoutMs = 15000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/json/version`);
      if (res.ok) return true;
    } catch (_) { /* not up yet */ }
    await sleep(250);
  }
  return false;
}

async function launchAndConnect(executablePath, profileDir) {
  const chromium = require('playwright-core').chromium;

  const port = await findFreePort().catch(() => null);
  if (!port) return null;

  try { fs.mkdirSync(profileDir, { recursive: true }); } catch (_) { /* ignore */ }

  const child = spawn(executablePath, [
    `--remote-debugging-port=${port}`,
    `--user-data-dir=${profileDir}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-features=Translate,MediaRouter',
    '--disable-background-networking',
    '--disable-sync',
    '--window-size=1280,900',
    // Park the window far off-screen: the page runs for real, but is never shown.
    '--window-position=-32000,-32000',
    'about:blank',
  ], { detached: false, stdio: 'ignore' });

  if (!(await waitForDebugEndpoint(port))) {
    killTree(child.pid);
    return null;
  }

  child.on('error', () => { /* the launch failure is handled by the debug-endpoint wait */ });

  const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  browser.on('disconnected', () => { if (session && session.browser === browser) session = null; });
  const context = browser.contexts()[0] || (await browser.newContext());
  return { child, browser, context, port };
}

let session = null;
let starting = null;

process.once('exit', () => {
  if (session && session.child) killTree(session.child.pid);
});
for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) {
  process.once(signal, () => {
    if (session && session.child) killTree(session.child.pid);
    process.exit(0);
  });
}

function cleanOldProfiles() {
  try {
    const cutoff = Date.now() - 2 * 24 * 60 * 60 * 1000;
    for (const entry of fs.readdirSync(CACHE_DIR, { withFileTypes: true })) {
      if (!entry.isDirectory() || !entry.name.startsWith('browser-profile')) continue;
      const full = path.join(CACHE_DIR, entry.name);
      try {
        if (fs.statSync(full).mtimeMs < cutoff) fs.rmSync(full, { recursive: true, force: true });
      } catch (_) { /* in use, skip */ }
    }
  } catch (_) { /* ignore */ }
}

async function startSession() {
  const executablePath = findBrowser();
  cleanOldProfiles();
  if (!executablePath) return null;

  let started = await launchAndConnect(executablePath, PROFILE_DIR);
  if (!started) {
    // A previous run may have died while holding the profile — clear it and retry.
    killStaleProfileBrowsers(PROFILE_DIR);
    await sleep(1200);
    started = await launchAndConnect(executablePath, PROFILE_DIR);
  }
  if (!started) started = await launchAndConnect(executablePath, `${PROFILE_DIR}-run`);
  return started;
}

async function ensureSession() {
  if (session) return session;
  if (!starting) {
    starting = startSession()
      .then((started) => { session = started; return session; })
      .catch(() => null)
      .finally(() => { starting = null; });
  }
  return starting;
}

async function closeSession() {
  if (!session) return;
  const current = session;
  session = null;
  try { await current.browser.close(); } catch (_) { /* ignore */ }
  killTree(current.child && current.child.pid);
}

/**
 * Opens `url` in the invisible browser and hands the live page to `handler`.
 *
 * `handler(page, navigate)` runs before navigation, so response listeners can be
 * attached first; call `navigate()` when ready. The tab is closed afterwards.
 * @returns {Promise<any|null>} handler result, or null when the browser is unavailable
 */
let busyPages = 0;
let idleTimer = null;

function scheduleIdleClose() {
  if (idleTimer) clearTimeout(idleTimer);
  idleTimer = setTimeout(() => {
    idleTimer = null;
    if (busyPages === 0) closeSession().catch(() => {});
  }, 2500);
  if (typeof idleTimer.unref === 'function') idleTimer.unref();
}

async function withPage(url, { timeoutMs = 75000 } = {}, handler) {
  const active = await ensureSession();
  if (!active) return null;

  busyPages += 1;
  if (idleTimer) { clearTimeout(idleTimer); idleTimer = null; }

  let page = null;
  try {
    page = await active.context.newPage();
    const navigate = () => page.goto(url, { waitUntil: 'domcontentloaded', timeout: Math.min(timeoutMs, 45000) });
    return await handler(page, navigate);
  } catch (_) {
    return null;
  } finally {
    if (page) await page.close().catch(() => {});
    busyPages -= 1;
    scheduleIdleClose();
  }
}

/**
 * Loads `url` in the invisible browser and returns the settled HTML once the
 * Cloudflare challenge has been cleared.
 * @returns {Promise<string|null>} final HTML, or null when unavailable / hard-blocked
 */
async function fetchPageHtml(url, { timeoutMs = 75000 } = {}) {
  return withPage(url, { timeoutMs }, async (page, navigate) => {
    await navigate();

    const deadline = Date.now() + timeoutMs;
    let html = '';
    while (Date.now() < deadline) {
      html = await page.content().catch(() => '');
      const text = ((await page.evaluate(() => document.body && document.body.innerText).catch(() => '')) || '').slice(0, 4000);
      if (HARD_BLOCK_TEXT.test(text)) return null;
      if (html && !CHALLENGE_TEXT.test(text) && !/challenges\.cloudflare\.com/.test(html) && text.trim().length > 120) {
        // Give late-rendered markup (lazy sections) a moment to finish.
        await sleep(1200);
        return await page.content().catch(() => html);
      }
      await sleep(750);
    }
    return null;
  });
}

module.exports = { fetchPageHtml, withPage, closeSession, findBrowser, PROFILE_DIR };
