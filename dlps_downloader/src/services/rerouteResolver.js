const axios = require('axios');
const cheerio = require('cheerio');
const fs = require('fs');
const path = require('path');
const logger = require('../utils/logger');

/**
 * Extracts a password from a resolved host page's HTML.
 * Looks for common patterns: "Password: xxx", "Password:xxx", input fields, etc.
 */
function extractPasswordFromHtml(html) {
  if (!html) return '';
  const $ = cheerio.load(html);
  const bodyText = $('body').text() || '';

  // Pattern 1: "Password: <value>" or "Password:<value>" (case insensitive)
  const pwMatch = bodyText.match(/[Pp]assword\s*[:\s]+([^\s<>"']{2,60})/i);
  if (pwMatch) return pwMatch[1].trim();

  // Pattern 2: Look in input fields named password or with placeholder containing password
  const pwInput = $('input[type="password"], input[name*="pass" i], input[placeholder*="password" i]');
  if (pwInput.length > 0) {
    const val = pwInput.attr('value') || '';
    if (val) return val;
    // Check next sibling text for the actual password value
    const nextText = pwInput.next().text().trim();
    if (nextText && nextText.length < 60 && !nextText.includes('password')) {
      // Could be the password shown next to the field
    }
  }

  // Pattern 3: Look for the text right after "Password" label in the same block
  const pwBlocks = bodyText.match(/[Pp]assword[^a-zA-Z]{0,5}([^\s]{4,60})/g) || [];
  for (const block of pwBlocks) {
    const m = block.match(/([^\s]{4,60})$/);
    if (m && m[1].length >= 4) return m[1].trim();
  }

  return '';
}

function sanitizeFichierUrl(url) {
  if (!url) return url;
  if (/1fichier\.com|1file/i.test(url)) {
    const match = url.match(/^(https?:\/\/(?:[a-z0-9]+\.)?(?:1fichier\.com|1file\.com)\/(?:\?|#)?)([a-z0-9]{5,20})/i);
    if (match) {
      return `https://1fichier.com/?${match[2].toLowerCase()}`;
    }
  }
  return url;
}

// Regular expression to match allowed download host domains
const DOWNLOAD_HOST_REGEX = /^https?:\/\/(?:www\.)?(?:1fichier\.com|datanodes\.to|mediafire\.com|rootz\.so|akirabox\.(?:com|to)|vikingfile\.com|mega\.nz|buzzheavier\.com|filekeeper\.net|datavaults\.co)\//i;

// Regular expression to find URLs inside plain text
const PLAIN_TEXT_URL_REGEX = /https?:\/\/(?:www\.)?(?:1fichier\.com|datanodes\.to|mediafire\.com|rootz\.so|akirabox\.(?:com|to)|vikingfile\.com|mega\.nz|buzzheavier\.com|filekeeper\.net|datavaults\.co)\/[^\s"'<>]+/gi;

const USER_AGENT = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36';

const { execSync } = require('child_process');

/**
 * Extracts URL from onclick attribute containing string concatenation (e.g. this.href='https://' + 'akirabox' + '.com/...')
 */
function extractUrlFromOnclick(onclickStr) {
  if (!onclickStr) return null;
  const match = onclickStr.match(/this\.href\s*=\s*([^;]+)/);
  if (match) {
    const expr = match[1].trim();
    // Extract all single/double quoted string literals and join them
    const regex = /'([^']*)'|"([^"]*)"/g;
    let parts = [];
    let m;
    while ((m = regex.exec(expr)) !== null) {
      parts.push(m[1] !== undefined ? m[1] : m[2]);
    }
    if (parts.length > 0) {
      const url = parts.join('');
      if (url.startsWith('http')) {
        return url;
      }
    }
  }
  return null;
}

/**
 * Extracts URL from an anchor element, handling href, data-domain + data-path,
 * secure-split (data-d1, data-d2, ... + data-path), and onclick concatenation.
 */
function extractUrlFromElement($, el) {
  const $el = $(el);
  const href = ($el.attr('href') || '').trim();
  const dataDomain = ($el.attr('data-domain') || '').trim();
  const dataPath = ($el.attr('data-path') || '').trim();
  const onclickAttr = ($el.attr('onclick') || '').trim();

  // Check for secure-split: data-d1, data-d2, ...
  const dParts = [];
  let dIdx = 1;
  while ($el.attr(`data-d${dIdx}`) || $el.attr(`data-domain${dIdx}`)) {
    const val = ($el.attr(`data-d${dIdx}`) || $el.attr(`data-domain${dIdx}`)).trim();
    dParts.push(val);
    dIdx++;
  }
  if (dParts.length > 0) {
    const domain = dParts.join('');
    const path = dataPath || '';
    const full = domain + path;
    if (full.startsWith('http')) return full;
  }

  if (dataDomain && dataPath) {
    return dataDomain + dataPath;
  }
  if (onclickAttr) {
    const onclickUrl = extractUrlFromOnclick(onclickAttr);
    if (onclickUrl) return onclickUrl;
  }
  if (href && href !== '#' && !href.startsWith('javascript:')) {
    return href;
  }
  return '';
}

async function fetchRawHtml(url) {
  let responseHtml = '';
  try {
    const res = await axios.get(url, {
      headers: { 'User-Agent': USER_AGENT }
    });
    responseHtml = res.data;
  } catch (axiosErr) {
    if (axiosErr.response && typeof axiosErr.response.data === 'string') {
      responseHtml = axiosErr.response.data;
    }
    if (!responseHtml || (!responseHtml.includes('Just a moment...') && !responseHtml.includes('challenges.cloudflare.com'))) {
      try {
        const cmd = `curl -s -L -A "${USER_AGENT}" "${url}"`;
        const stdout = execSync(cmd, { encoding: 'utf-8', maxBuffer: 10 * 1024 * 1024 });
        if (stdout && stdout.trim().length > 0) {
          responseHtml = stdout;
        }
      } catch (curlErr) {
        // ignore
      }
    }
    if (!responseHtml) {
      throw axiosErr;
    }
  }

  if (responseHtml && (responseHtml.includes('Just a moment...') || responseHtml.includes('challenges.cloudflare.com'))) {
    const err = new Error('Cloudflare Turnstile challenge detected.');
    err.isCloudflare = true;
    err.html = responseHtml;
    throw err;
  }
  return responseHtml;
}

const { fetchPageHtml } = require('./headlessBrowser');

const MEMORY_CACHE = new Map();
const FAILED_URLS = new Map();

/**
 * Opens a real Edge browser to solve Cloudflare Turnstile and returns the page HTML.
 * Falls back to manual-save instructions if Edge/Playwright is unavailable.
 * Returns null if the page is a Cloudflare error (rate limit, access denied, etc.).
 */
async function fetchWithBrowser(url, manualHtmlPath) {
  // Runs the user's installed Chrome/Edge invisibly (off-screen window, private profile)
  // over CDP: it clears Cloudflare's challenge automatically, so no captcha or manual
  // HTML save is ever required.
  const html = await fetchPageHtml(url).catch(() => null);
  if (!html) return null;

  // Cache the resolved HTML so we don't have to solve the challenge again.
  if (manualHtmlPath) {
    try {
      const cacheDir = path.dirname(manualHtmlPath);
      if (!fs.existsSync(cacheDir)) fs.mkdirSync(cacheDir, { recursive: true });
      fs.writeFileSync(manualHtmlPath, html, 'utf-8');
    } catch (_) { /* ignore cache write errors */ }
  }

  return html;
}

/**
 * Fetches the reroute URL and extracts all valid direct download links.
 * Also extracts any password shown on the resolved page.
 * 
 * @param {string} rerouteUrl 
 * @param {Set<string>} visited
 * @param {number} depth
 * @returns {Promise<Array<{label: string, url: string, password?: string}>>}
 */
async function resolveReroute(rerouteUrl, visited = new Set(), depth = 0) {
  if (depth > 2) return [];
  if (visited.has(rerouteUrl)) return [];
  visited.add(rerouteUrl);

  if (MEMORY_CACHE.has(rerouteUrl)) {
    return MEMORY_CACHE.get(rerouteUrl);
  }
  if (FAILED_URLS.has(rerouteUrl)) {
    throw FAILED_URLS.get(rerouteUrl);
  }

  const archiveIdMatch = rerouteUrl.match(/\/archives\/(\d+)/);
  const archiveId = archiveIdMatch ? archiveIdMatch[1] : '';
  const manualRerouteHtmlPath = archiveId 
    ? path.join(__dirname, `../../data/cache/manual_html/reroute-${archiveId}.html`)
    : '';

  try {
    let htmlData;
    let usedApi = false;
    if (manualRerouteHtmlPath && fs.existsSync(manualRerouteHtmlPath)) {
      htmlData = fs.readFileSync(manualRerouteHtmlPath, 'utf-8');
    } else {
      // 1. Try WordPress REST API first (since it is Turnstile-free)
      if (archiveId) {
        try {
          const apiRes = await axios.get(`https://downloadgameps3.net/wp-json/wp/v2/posts/${archiveId}`, {
            headers: { 'User-Agent': USER_AGENT }
          });
          if (apiRes.data && apiRes.data.content && apiRes.data.content.rendered) {
            htmlData = apiRes.data.content.rendered;
            usedApi = true;
          }
        } catch (apiErr) {
          // Ignore and fallback
        }
      }

      // 2. Try direct fetch (axios + curl)
      if (!htmlData) {
        try {
          htmlData = await fetchRawHtml(rerouteUrl);
        } catch (fetchErr) {
          if (fetchErr.isCloudflare || (fetchErr.message && fetchErr.message.includes('Cloudflare Turnstile'))) {
            // 3. Browser-based Turnstile bypass as last resort
            const browserHtml = await fetchWithBrowser(rerouteUrl, manualRerouteHtmlPath);
            if (browserHtml) {
              htmlData = browserHtml;
            } else {
              const cfErr = new Error(`Cloudflare blocked resolving reroute URL (possibly rate-limited).
To bypass this block, please follow these steps:
1. Open this URL in your web browser:
   ${rerouteUrl}
2. Right-click anywhere on the page, select "View Page Source" (or save page as HTML).
3. Copy all HTML source code and save it exactly to this file path:
   ${manualRerouteHtmlPath}
4. Re-run your download command!`);
              cfErr.isCloudflare = true;
              FAILED_URLS.set(rerouteUrl, cfErr);
              throw cfErr;
            }
          } else {
            throw fetchErr;
          }
        }
      }
    }

    if (htmlData && (htmlData.includes('Just a moment...') || htmlData.includes('challenges.cloudflare.com'))) {
      // HTML still contains Cloudflare challenge — try browser bypass
      const browserHtml = await fetchWithBrowser(rerouteUrl, manualRerouteHtmlPath);
      if (browserHtml) {
        htmlData = browserHtml;
      } else {
        const cfErr = new Error(`Cloudflare Turnstile challenge blocked resolving reroute URL.
To bypass this block, please follow these steps:
1. Open this URL in your web browser:
   ${rerouteUrl}
2. Right-click anywhere on the page, select "View Page Source" (or save page as HTML).
3. Copy all HTML source code and save it exactly to this file path:
   ${manualRerouteHtmlPath}
4. Re-run your download command!`);
        cfErr.isCloudflare = true;
        FAILED_URLS.set(rerouteUrl, cfErr);
        throw cfErr;
      }
    }

    const extractLinks = (html) => {
      const $ = cheerio.load(html);
      const linksMap = new Map();
      const nestedReroutes = [];
      const REROUTE_ARCHIVE_REGEX = /downloadgameps3\.net\/archives\/(\d+)/i;

      $('a').each((_, el) => {
        const url = extractUrlFromElement($, el);
        const label = $(el).text().trim() || 'Link';
        if (url) {
          if (DOWNLOAD_HOST_REGEX.test(url)) {
            linksMap.set(sanitizeFichierUrl(url), label);
          } else if (REROUTE_ARCHIVE_REGEX.test(url) && url !== rerouteUrl) {
            nestedReroutes.push(url);
          }
        }
      });

      const pageText = $('body').text() || '';
      const textUrls = pageText.match(PLAIN_TEXT_URL_REGEX) || [];
      for (const url of textUrls) {
        const trimmedUrl = url.trim();
        const cleanUrl = sanitizeFichierUrl(trimmedUrl);
        if (!linksMap.has(cleanUrl)) {
          linksMap.set(cleanUrl, 'Text Link');
        }
      }

      return { linksMap, nestedReroutes };
    };

    let { linksMap, nestedReroutes } = extractLinks(htmlData);

    // Fallback if API returned no links (often due to dynamic JS / custom data attribute filtering in WP REST API)
    if (usedApi && linksMap.size === 0 && nestedReroutes.length === 0 && !manualRerouteHtmlPath) {
      try {
        const rawHtml = await fetchRawHtml(rerouteUrl);
        const res = extractLinks(rawHtml);
        linksMap = res.linksMap;
        nestedReroutes = res.nestedReroutes;
      } catch (err) {
        // ignore
      }
    }

    // Resolve nested reroutes recursively
    for (const nestedUrl of nestedReroutes) {
      try {
        const nestedLinks = await resolveReroute(nestedUrl, visited, depth + 1);
        for (const nl of nestedLinks) {
          if (!linksMap.has(nl.url)) {
            linksMap.set(nl.url, nl.label);
          }
        }
      } catch (e) {
        // Ignore nested resolution failures
      }
    }

    // If no links were resolved, check if this page is a text installation guide
    if (linksMap.size === 0) {
      const $ = cheerio.load(htmlData);
      const entryContent = $('.entry-content, .post-content, .post-entry').first();
      const entryText = entryContent.length > 0 ? entryContent.text() : $('body').text();
      
      const cleanLines = entryText.split('\n')
        .map(line => line.trim())
        .filter(line => {
          const lower = line.toLowerCase();
          return line.length > 0 && 
                 !lower.includes('guide bypass google drive') && 
                 !lower.includes('guide fix download link') && 
                 !lower.includes('guide fix error') && 
                 !lower.includes('dlpsgame.com') && 
                 !lower.includes('link download free') &&
                 !lower.includes('skip to content');
        });
      
      const bodyText = cleanLines.join('\n').trim();
      const textLower = bodyText.toLowerCase();
      
      const isGuide = textLower.includes('how to install') || 
                      textLower.includes('guide') || 
                      textLower.includes('readme') || 
                      textLower.includes('instruction') || 
                      textLower.includes('unlock all dlc');
                      
      if (isGuide && bodyText.length > 10) {
        const resolvedLinks = [{ label: 'INSTALL_GUIDE', url: `text_guide:${bodyText}` }];
        return resolvedLinks;
      }
    }

    // Convert Map back to list of objects
    const resolvedLinks = [];
    linksMap.forEach((label, url) => {
      resolvedLinks.push({ label, url });
    });

    // Extract password from the resolved page HTML (override any existing password)
    const pagePassword = extractPasswordFromHtml(htmlData);
    if (pagePassword && pagePassword.length >= 4) {
      logger.info(`[Reroute] Found password on resolved page: "${pagePassword}"`);
      // Attach password to all links from this page
      for (const link of resolvedLinks) {
        link.password = pagePassword;
      }
      // If no links have password yet, set it on the first one as fallback
      if (!resolvedLinks.some(l => l.password)) {
        resolvedLinks.length > 0 && (resolvedLinks[0].password = pagePassword);
      }
    }

    MEMORY_CACHE.set(rerouteUrl, resolvedLinks);
    return resolvedLinks;
  } catch (err) {
    FAILED_URLS.set(rerouteUrl, err);
    if (err.isCloudflare || (err.message && err.message.includes('Cloudflare'))) {
      throw err;
    }
    const wrappedErr = new Error(`Failed to resolve reroute URL: ${rerouteUrl}. Error: ${err.message}`);
    FAILED_URLS.set(rerouteUrl, wrappedErr);
    throw wrappedErr;
  }
}

module.exports = {
  resolveReroute
};
