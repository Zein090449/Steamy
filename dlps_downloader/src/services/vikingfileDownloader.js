const { withPage } = require('./headlessBrowser');
const axios = require('axios');
const fs = require('fs');
const path = require('path');

const EDGE_CANDIDATES = [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
];

function findEdge() {
  for (const p of EDGE_CANDIDATES) {
    if (fs.existsSync(p)) return p;
  }
  return null;
}

const USER_AGENT = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36';

// Injected before any page script runs — hides all Playwright/automation signals
// that Cloudflare Turnstile uses to detect bots.
const STEALTH_SCRIPT = `
  Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
  Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
  Object.defineProperty(navigator, 'languages', { get: () => ['ko-KR', 'ko', 'en-US', 'en'] });
  window.chrome = { runtime: {} };
`;

function extractVikingfileHash(url) {
  const match = url.match(/vik(?:1ng|ing)file\.(?:com|site)\/f\/([a-zA-Z0-9_\-]+)/i);
  return match ? match[1] : null;
}

/**
 * Downloads a file from vikingfile.com using a real Edge browser to solve
 * Cloudflare Turnstile. The browser window is visible so Turnstile can
 * verify the session. It auto-solves within ~5 seconds for normal sessions.
 * If Turnstile shows a checkbox challenge, the user can click it.
 */
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function downloadFromVikingfile(fileUrl, destDir, onProgress, onStatus) {
  const hash = extractVikingfileHash(fileUrl);
  if (!hash) throw new Error(`Cannot parse vikingfile hash from URL: ${fileUrl}`);

  const pageUrl = `https://vik1ngfile.site/f/${hash}`;
  const { directUrl } = await resolveVikingfileViaInvisibleBrowser(pageUrl, onStatus);

  if (onStatus) onStatus('Got download URL — starting transfer...');
  if (!fs.existsSync(destDir)) fs.mkdirSync(destDir, { recursive: true });

  let filename = null;
  try { filename = decodeURIComponent(path.basename(new URL(directUrl).pathname)); } catch (_) {}
  filename = (filename || `vikingfile_${hash}`).replace(/[\/:*?"<>|]/g, '_').trim();
  const destPath = path.join(destDir, filename);

  const MAX_ATTEMPTS = 4;
  let lastError = null;

  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt += 1) {
    const existing = fs.existsSync(destPath) ? fs.statSync(destPath).size : 0;
    const headers = { 'User-Agent': USER_AGENT, Referer: 'https://vik1ngfile.site/' };
    if (existing > 0) headers.Range = `bytes=${existing}-`;

    let response;
    try {
      response = await axios.get(directUrl, {
        responseType: 'stream',
        headers,
        validateStatus: (status) => status >= 200 && status < 400
      });
    } catch (error) {
      lastError = error;
      if (attempt === MAX_ATTEMPTS) break;
      if (onStatus) onStatus(`Transfer could not start — retrying (${attempt + 1}/${MAX_ATTEMPTS})…`);
      await sleep(1500 * attempt);
      continue;
    }

    const resumed = response.status === 206;
    let total = 0;
    if (resumed) {
      const contentRange = response.headers['content-range'] || '';
      const match = contentRange.match(/\/(\d+)\s*$/);
      total = match ? parseInt(match[1], 10) : 0;
    } else {
      total = parseInt(response.headers['content-length'], 10) || 0;
    }

    // The host ignored our Range request: start the file over.
    if (!resumed && existing > 0) {
      try { fs.rmSync(destPath, { force: true }); } catch (_) { /* ignore */ }
    }

    const offset = resumed ? existing : 0;
    if (total > 0 && offset >= total && offset > 0) {
      response.data.destroy();
      return { destPath, filename, size: offset, directUrl, skipped: true };
    }

    let written = offset;
    const writer = fs.createWriteStream(destPath, { flags: offset > 0 ? 'a' : 'w' });
    try {
      await new Promise((resolve, reject) => {
        response.data.on('data', (chunk) => {
          written += chunk.length;
          if (onProgress) onProgress(written, total || written);
        });
        response.data.once('error', reject);
        writer.once('error', reject);
        writer.once('finish', resolve);
        response.data.pipe(writer);
      });
      return { destPath, filename, size: written, directUrl };
    } catch (error) {
      lastError = error;
      try { response.data.destroy(); } catch (_) { /* ignore */ }
      try { writer.destroy(); } catch (_) { /* ignore */ }
      if (attempt === MAX_ATTEMPTS) break;
      if (onStatus) {
        const mb = (written / 1024 / 1024).toFixed(1);
        onStatus(`Transfer interrupted at ${mb}MB — resuming (${attempt + 1}/${MAX_ATTEMPTS})…`);
      }
      await sleep(1500 * attempt);
    }
  }

  throw lastError || new Error('The Vikingfile download could not be completed.');
}

/**
 * Resolves only the direct download URL from a vikingfile link via browser.
 * Does NOT download the file — use this when handing off to FDM.
 */
async function resolveVikingfileDirectUrl(fileUrl, onStatus) {
  const hash = extractVikingfileHash(fileUrl);
  if (!hash) throw new Error(`Cannot parse vikingfile hash from URL: ${fileUrl}`);

  const pageUrl = `https://vik1ngfile.site/f/${hash}`;
  const edgePath = process.env.EDGE_PATH || findEdge();
  if (!edgePath) throw new Error('Microsoft Edge not found.');

  if (onStatus) onStatus('Verifying download link...');

  const directUrl = (await resolveVikingfileViaInvisibleBrowser(pageUrl, onStatus)).directUrl;

  if (!directUrl) throw new Error(`Failed to resolve Vikingfile direct URL for: ${fileUrl}`);

  let filename = null;
  try { filename = decodeURIComponent(path.basename(new URL(directUrl).pathname)); } catch (_) {}
  filename = (filename || `vikingfile_${hash}`).replace(/[\\/:*?"<>|]/g, '_').trim();

  return { directUrl, filename };
}

/**
 * Solves vikingfile's Turnstile in an invisible browser and captures the direct file URL
 * from its POST /f/<hash> response. Nothing is shown and no captcha is ever presented.
 */
async function resolveVikingfileViaInvisibleBrowser(pageUrl, onStatus) {
  if (onStatus) onStatus('Resolving download link...');

  const directUrl = await withPage(pageUrl, { timeoutMs: 60000 }, (page, navigate) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Vikingfile Turnstile timed out after 60s.')), 60000);
    const done = (url) => { clearTimeout(timer); resolve(url); };

    page.on('response', async (response) => {
      if (response.url().includes('/f/') && response.request().method() === 'POST') {
        try {
          const json = JSON.parse(await response.text());
          if (json.link) done(decodeURIComponent(json.link));
        } catch (_) { /* not the response we want */ }
      }
    });
    page.on('close', () => { clearTimeout(timer); reject(new Error('Browser closed before the Vikingfile link was captured.')); });

    navigate().catch(reject);
    // If the page does not fire its own request, nudge it with a click.
    setTimeout(() => { page.click('text=Download', { timeout: 4000 }).catch(() => {}); }, 7000);
  })).catch(() => null);

  if (!directUrl) throw new Error('Vikingfile link could not be resolved automatically.');
  return { directUrl };
}

module.exports = { downloadFromVikingfile, resolveVikingfileDirectUrl, extractVikingfileHash };
