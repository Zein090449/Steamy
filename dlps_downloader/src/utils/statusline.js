/**
 * Progress output that survives being run without a terminal.
 *
 * ora spinners write carriage-return updates with no newline, so when the CLI is started
 * by a GUI (pipes, not a TTY) ora swallows everything and the download looks frozen.
 * This helper prints plain, rate-limited lines in that case and stays silent when a real
 * terminal is driving an interactive spinner.
 */

let lastLoggedAt = 0;
let lastText = '';

function logStatus(text, { force = false, minIntervalMs = 1500 } = {}) {
  if (!text) return false;
  const clean = String(text).replace(/\s+/g, ' ').trim();
  if (clean.length === 0 || clean === lastText) return false;
  if (process.stdout.isTTY && !force) return false;

  const now = Date.now();
  if (!force && now - lastLoggedAt < minIntervalMs) return false;

  lastLoggedAt = now;
  lastText = clean;
  try {
    process.stdout.write(clean + '\n');
  } catch (_) { /* ignore closed pipes */ }
  return true;
}

module.exports = { logStatus };
