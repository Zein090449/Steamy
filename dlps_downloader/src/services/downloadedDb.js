const fs = require('fs');
const path = require('path');
const { XMLParser } = require('fast-xml-parser');
const { normalizeTitle } = require('../utils/titleNormalizer');
const { platformDataPath } = require('./platformConfig');

// Per-platform downloaded library, e.g. data/downloaded-ps5.xml
const DB_PATH = platformDataPath('downloaded', 'xml');

/**
 * Loads the list of successfully downloaded games from downloaded.xml.
 * 
 * @returns {Array<{title: string, fileName: string, ppsa: string, password: string, downloadedAt: string, source: string, region: string, normalizedTitle: string}>}
 */
function loadDownloadedGames() {
  if (!fs.existsSync(DB_PATH)) {
    return [];
  }

  try {
    const xmlData = fs.readFileSync(DB_PATH, 'utf-8');
    if (!xmlData.trim()) {
      return [];
    }
    const parser = new XMLParser({
      ignoreAttributes: false,
      isArray: (name) => ['Game'].includes(name)
    });
    const jsonObj = parser.parse(xmlData);
    const games = jsonObj.Downloaded?.Game || [];
    
    return games.map(g => ({
      title: g.Title || '',
      fileName: g.FileName || '',
      ppsa: g.PPSA || '',
      password: g.Password || '',
      downloadedAt: g.DownloadedAt || '',
      source: g.Source || '',
      region: g.Region || '',
      normalizedTitle: normalizeTitle(g.Title || '')
    }));
  } catch (err) {
    console.error(`[ERROR] Failed to parse XML file at ${DB_PATH}:`, err.message);
    return [];
  }
}

/**
 * Escapes special XML characters.
 */
function escapeXml(unsafe) {
  if (!unsafe) return '';
  return unsafe.toString().replace(/[<>&'"]/g, (c) => {
    switch (c) {
      case '<': return '&lt;';
      case '>': return '&gt;';
      case '&': return '&amp;';
      case '\'': return '&apos;';
      case '"': return '&quot;';
      default: return c;
    }
  });
}

/**
 * Saves the given downloaded games list to downloaded.xml.
 */
function saveDownloadedGames(games) {
  let xml = '<?xml version="1.0" standalone="yes"?>\n<Downloaded>\n';
  for (const g of games) {
    xml += '  <Game>\n';
    xml += `    <Title>${escapeXml(g.title)}</Title>\n`;
    xml += `    <FileName>${escapeXml(g.fileName)}</FileName>\n`;
    xml += `    <PPSA>${escapeXml(g.ppsa)}</PPSA>\n`;
    xml += `    <Password>${escapeXml(g.password)}</Password>\n`;
    xml += `    <DownloadedAt>${escapeXml(g.downloadedAt)}</DownloadedAt>\n`;
    xml += `    <Source>${escapeXml(g.source)}</Source>\n`;
    xml += `    <Region>${escapeXml(g.region)}</Region>\n`;
    xml += '  </Game>\n';
  }
  xml += '</Downloaded>\n';
  
  const dir = path.dirname(DB_PATH);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }

  // Backup existing file before overwrite
  if (fs.existsSync(DB_PATH)) {
    try {
      const stats = fs.statSync(DB_PATH);
      if (stats.size > 100) {
        fs.copyFileSync(DB_PATH, DB_PATH + '.bak');
      }
    } catch (e) {}
  }

  fs.writeFileSync(DB_PATH, xml, 'utf-8');
}

/**
 * Adds a new game entry to downloaded.xml.
 */
function addDownloadedGame({ title, fileName, ppsa, password, source, region }) {
  const games = loadDownloadedGames();
  
  let existingGame = null;
  const exists = games.some(g => {
    if (normalizeTitle(g.title) === normalizeTitle(title)) {
      return true;
    }
    if (source === 'Manual' || source === 'Manual (Dupe)') {
      return false;
    }
    if (ppsa && ppsa !== 'Unknown' && g.ppsa && g.ppsa !== 'Unknown') {
      if (g.ppsa === ppsa && g.region === region) {
        existingGame = g;
        return true;
      }
    }
    return false;
  });
  if (exists) {
    if (existingGame && normalizeTitle(existingGame.title) !== normalizeTitle(title)) {
      existingGame.title = title;
      saveDownloadedGames(games);
    }
    return;
  }

  games.push({
    title,
    fileName,
    ppsa,
    password,
    downloadedAt: new Date().toISOString(),
    source,
    region
  });

  saveDownloadedGames(games);
}

/**
 * Removes a game entry from downloaded.xml by title.
 */
function removeDownloadedGame(title) {
  const games = loadDownloadedGames();
  const targetNorm = normalizeTitle(title);
  const filtered = games.filter(g => normalizeTitle(g.title) !== targetNorm);
  if (filtered.length < games.length) {
    saveDownloadedGames(filtered);
    return true;
  }
  return false;
}

/**
 * Removes a game entry from downloaded.xml matching title, normalizedTitle, ppsa, or titleQuery.
 * Returns array of removed games.
 */
function removeDownloadedGameForGame({ title = '', normalizedTitle = '', ppsa = '', titleQuery = '' } = {}) {
  const normTitle = normalizedTitle || (title ? normalizeTitle(title) : '');
  const normQuery = titleQuery ? normalizeTitle(titleQuery) : '';
  const ppsaUpper = (ppsa && ppsa !== 'Unknown') ? ppsa.toUpperCase() : '';

  const games = loadDownloadedGames();
  if (games.length === 0) return [];

  const toRemove = [];
  const kept = [];

  for (const g of games) {
    const gNorm = g.normalizedTitle || normalizeTitle(g.title || '');
    const gPpsa = (g.ppsa && g.ppsa !== 'Unknown') ? g.ppsa.toUpperCase() : '';

    let matched = false;
    if (normTitle && gNorm === normTitle) matched = true;
    else if (normQuery && gNorm === normQuery) matched = true;
    else if (ppsaUpper && gPpsa && gPpsa === ppsaUpper) matched = true;

    if (matched) {
      toRemove.push(g);
    } else {
      kept.push(g);
    }
  }

  // Fallback: partial match if titleQuery is >= 3 chars and uniquely matches
  if (toRemove.length === 0 && normQuery && normQuery.length >= 3) {
    const candidateIndices = [];
    kept.forEach((g, idx) => {
      const gNorm = g.normalizedTitle || normalizeTitle(g.title || '');
      if (gNorm.includes(normQuery) || normQuery.includes(gNorm)) {
        candidateIndices.push(idx);
      }
    });
    if (candidateIndices.length === 1) {
      const [idx] = candidateIndices;
      toRemove.push(kept[idx]);
      kept.splice(idx, 1);
    }
  }

  if (toRemove.length > 0) {
    saveDownloadedGames(kept);
  }

  return toRemove;
}

module.exports = {
  loadDownloadedGames,
  saveDownloadedGames,
  addDownloadedGame,
  removeDownloadedGame,
  removeDownloadedGameForGame
};
