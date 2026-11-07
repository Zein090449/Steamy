const fs = require('fs');
const path = require('path');
const { execSync, spawn } = require('child_process');
const { deriveVersionFromParam, deriveTitleNameFromParam } = require('../utils/versionParser');

const BIN_DIR = path.join(__dirname, '../../bin');
// Try Bandizip first; fall back to 7-Zip if not installed
const BZ_EXE_PATH = 'C:\\\\Program Files\\\\Bandizip\\\\bz.exe';
const B7Z_EXE_PATH = 'C:\\\\Program Files\\\\7-Zip\\\\7z.exe';

// `bz l` prints one row per archived entry. Games with thousands of files (e.g.
// per-animation assets) produce listings well over execSync's 1 MB default
// maxBuffer; overflowing throws ENOBUFS, which the listing helpers catch and
// silently treat as "listing failed", derailing param.json discovery. Give the
// listing calls plenty of headroom.
const LIST_MAXBUFFER = 256 * 1024 * 1024;

function sanitizeFileName(name) {
  let cleanName = name
    .replace(/[®™©]/g, '')
    .replace(/\\((c|tm|r)\\)/gi, '')
    .replace(/:/g, ' - ')
    .replace(/\\s+/g, ' ')
    .trim();
  return cleanName.replace(/[\\\\/*?\"<>|]/g, '_').trim();
}

function findParamJson(dir) {
  if (!fs.existsSync(dir)) return null;
  for (const file of fs.readdirSync(dir)) {
    const fullPath = path.join(dir, file);
    if (fs.statSync(fullPath).isDirectory()) {
      const found = findParamJson(fullPath);
      if (found) return found;
    } else if (file.toLowerCase() === 'param.json') {
      return fullPath;
    }
  }
  return null;
}

function requireBz() {
  if (fs.existsSync(BZ_EXE_PATH)) {
    return BZ_EXE_PATH;
  }
  if (fs.existsSync(B7Z_EXE_PATH)) {
    return B7Z_EXE_PATH;
  }
  throw new Error(`Bandizip (bz.exe) not found at: ${BZ_EXE_PATH}, and 7z.exe not found at: ${B7Z_EXE_PATH}`);
}

const PASSWORD_FALLBACKS = [
  // Most common dlpsgame.com / downloadgameps3.com passwords seen across many games
  'downloadgameps3.com', 'DownloadGamePS3.com', '[downloadgameps3.com]', '[DownloadGamePS3.com]',
  'www.downloadgameps3.com', 'www.DownloadGamePS3.com',
  'DLPSGAME.COM', 'dlpsgame.com', 'www.DLPSGAME.COM', 'www.dlpsgame.com',
  '[DLPSGAME.COM]', '[dlpsgame.com]',
  // Password format shown on resolved host pages: (DOMAIN)
  '(DLPSGAME.COM)', '(downloadgameps3.com)', '(DownloadGamePS3.com)', '(DLPGAME.COM)',
  // Older/spoofed domain variants
  'DLPGAME.COM', 'dlpgame.com', 'www.DLPGAME.COM', 'www.dlpgame.com',
  '[DLPGAME.COM]', '[dlpgame.com]',
  // Individual uploader passwords seen on specific games
  'hako', 'vnsharing', '1f-alvro', 'dvdiso.club'
];

function buildCandidates(password) {
  const candidates = password ? [password] : [];
  for (const fb of PASSWORD_FALLBACKS) {
    if (!candidates.includes(fb)) candidates.push(fb);
  }
  return candidates;
}

/** Returns true if the archive contains a .exfat file (fast listing, no extraction). */
function archiveContainsExfat(archivePath) {
  try {
    const output = execSync(`"${requireBz()}" l "${archivePath}"`, {
      encoding: 'utf-8',
      stdio: ['pipe', 'pipe', 'ignore'],
      maxBuffer: LIST_MAXBUFFER
    });
    return output.toLowerCase().includes('.exfat');
  } catch (e) {
    return false;
  }
}

/** Checks if an archive is password protected. */
async function isArchiveEncrypted(filePath) {
  const bz = requireBz();
  try {
    execSync(`"${bz}" t -y "${filePath}"`, { stdio: 'ignore' });
    return false;
  } catch (e) {
    return true;
  }
}

/** Extracts any archive (RAR/ZIP/7z) to a destination directory using Bandizip or 7z. */
async function extractRarArchive(rarFilePath, destFolder, password, onProgress) {
  const bz = requireBz();

  if (!fs.existsSync(destFolder)) {
    fs.mkdirSync(destFolder, { recursive: true });
  }

  const args = ['x', '-y'];
  if (password) {
    // 7z (Igor Pavlov) accepts -p<pwd> without colon; Bandizip accepts -p:<pwd>.
    // For passwords with special chars (parens, brackets), use -p"<pwd>" form which
    // both archivers handle correctly via spawn arg array (no shell parsing).
    if (bz === B7Z_EXE_PATH) {
      args.push(`-p"${password}"`);
    } else {
      args.push(`-p:${password}`);
    }
  }
  args.push(`-o:${destFolder}`);
  args.push(rarFilePath);

  return new Promise((resolve, reject) => {
    const child = spawn(bz, args, { shell: false });

    let stderrOut = '';

    child.stdout.on('data', (data) => {
      if (onProgress) {
        const text = data.toString().trim();
        const lines = text.split('\n');
        for (const line of lines) {
          if (line.includes('Extracting')) {
            onProgress(line.trim());
          }
        }
      }
    });

    child.stderr.on('data', (data) => {
      stderrOut += data.toString();
    });

    child.on('close', (code) => {
      if (code !== 0) {
        reject(new Error(`Archive extraction failed: ${stderrOut.trim() || 'Code ' + code}`));
      } else {
        resolve();
      }
    });
  });
}

/** Lists an archive with optional password and returns the internal path of param.json, or null. */
function findParamPathInArchive(bz, archivePath, pwd) {
  // 7z needs -p"<pwd>" for passwords with special chars; Bandizip uses -p:<pwd>
  const pwdFlag = pwd ? (bz === B7Z_EXE_PATH ? `-p"${pwd}"` : `-p:${pwd}`) : '';
  try {
    const output = execSync(
      `"${bz}" l ${pwdFlag} "${archivePath}"`,
      { encoding: 'utf-8', stdio: ['pipe', 'pipe', 'ignore'], maxBuffer: LIST_MAXBUFFER }
    );
    // bz l rows: "YYYY-MM-DD HH:MM:SS Attr Size CompSize Name"
    // Size/CompSize are plain integers; the Name (which may contain spaces or
    // backslashes) is everything after the CompSize column.
    const rowRe = /^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\s+\S+\s+\d+\s+\d+\s+(.+)$/;
    for (const line of output.split(/\r?\n/)) {
      const m = line.match(rowRe);
      if (!m) continue;
      const name = m[1].trim();
      if (name && path.basename(name).toLowerCase() === 'param.json') {
        return name;
      }
    }
  } catch (e) {}
  return null;
}

function getDownloadDir() {
  return process.env.DOWNLOAD_DIR || path.join(__dirname, '../../downloads');
}

/** Extracts param.json from an archive and parses game metadata. */
async function getGameInfoFromArchive(rarFilePath, password, onProgress) {
  const bz = requireBz();
  const downloadDir = getDownloadDir();
  const tempDir = path.join(downloadDir, 'temp_param_' + Date.now());
  fs.mkdirSync(tempDir, { recursive: true });

  const candidates = buildCandidates(password);

  if (onProgress) onProgress('Reading archive file list...');

  // Step 1: find param.json's exact internal path via listing
  let paramInternalPath = findParamPathInArchive(bz, rarFilePath, '');
  let workingPassword = '';
  let encryptedFlag = false;

  if (!paramInternalPath) {
    // Listing failed — archive may be encrypted; try each password candidate
    encryptedFlag = true;
    for (const cand of candidates) {
      if (onProgress) onProgress(`Testing password: "${cand}"`);
      paramInternalPath = findParamPathInArchive(bz, rarFilePath, cand);
      if (paramInternalPath) {
        workingPassword = cand;
        break;
      }
    }
  }

  // Step 2: build list of paths to try, from most specific to broadest
  const pathsToTry = paramInternalPath
    ? [paramInternalPath, 'sce_sys/param.json', 'sce_sys\\\\param.json', '*param.json']
    : ['sce_sys/param.json', 'sce_sys\\\\param.json', '*param.json'];

  const testCandidates = encryptedFlag ? (workingPassword ? [workingPassword] : candidates) : ['', ...candidates];

  let success = false;

  if (onProgress) onProgress('Extracting param.json...');

  outer:
  for (const cand of testCandidates) {
    // 7z: -p"<pwd>" (no colon); Bandizip: -p:<pwd>
    const pwd = cand ? (bz === B7Z_EXE_PATH ? `-p"${cand}"` : `-p:${cand}`) : '';
    // 7z: -o"<path>" (no colon before quote); Bandizip: -o:<path>
    const oFlag = bz === B7Z_EXE_PATH ? `-o"${tempDir}"` : `-o:"${tempDir}"`;
    for (const target of pathsToTry) {
      for (const cmd of ['e', 'x']) {
        try {
          execSync(
            `"${bz}" ${cmd} -y ${pwd} ${oFlag} "${rarFilePath}" "${target}"`,
            { stdio: 'ignore' }
          );
          if (findParamJson(tempDir)) {
            workingPassword = cand;
            encryptedFlag = !!cand;
            success = true;
            break outer;
          }
        } catch (e) { /* try next */ }
      }
    }
  }

  if (!success) {
    try { fs.rmSync(tempDir, { recursive: true, force: true }); } catch (e) {}
    throw new Error('Failed to extract param.json from archive (incorrect password or corrupted archive).');
  }

  const paramPath = findParamJson(tempDir);
  if (!paramPath) {
    try { fs.rmSync(tempDir, { recursive: true, force: true }); } catch (e) {}
    throw new Error('param.json was not found inside the archive structure.');
  }

  try {
    const content = fs.readFileSync(paramPath, 'utf-8');
    const param = JSON.parse(content);

    const titleId = param.titleId || 'Unknown';
    const version = deriveVersionFromParam(param);

    let titleName = deriveTitleNameFromParam(param);
    if (!titleName) titleName = 'Unknown';

    try { fs.rmSync(tempDir, { recursive: true, force: true }); } catch (e) {}

    return {
      titleName: sanitizeFileName(titleName),
      titleId,
      version,
      encrypted: encryptedFlag,
      workingPassword
    };
  } catch (parseErr) {
    try { fs.rmSync(tempDir, { recursive: true, force: true }); } catch (e) {}
    throw new Error(`Failed to parse param.json: ${parseErr.message}`);
  }
}

/** Finds the working password for an archive. Returns empty string if the archive is not encrypted or no password works. */
async function findWorkingPassword(rarFilePath, passwordCandidates = [], onProgress) {
  const bz = requireBz();
  const logger = require('../utils/logger');

  const candidates = buildCandidates(passwordCandidates[0] || '');
  for (const c of passwordCandidates.slice(1)) {
    if (!candidates.includes(c)) candidates.unshift(c);
  }

  const testFast = (pwd) => {
    // 7z needs -p"<pwd>" for passwords with special chars; Bandizip uses -p:<pwd>
    const pwdFlag = pwd ? (bz === B7Z_EXE_PATH ? `-p"${pwd}"` : `-p:${pwd}`) : '';
    try {
      // 1. Test header decryption by listing the archive
      const output = execSync(
        `"${bz}" l ${pwdFlag} "${rarFilePath}"`,
        { encoding: 'utf-8', stdio: ['pipe', 'pipe', 'ignore'], maxBuffer: 1024 * 1024 * 10 }
      );

      // 2. Test data decryption on the first non-directory file found.
      // Use split-based parsing (same approach as findParamPathInArchive which works reliably).
      // 7z listing format: DATE TIME ATTRIB SIZE COMPSIZE PATH
      for (const line of output.split(/\r?\n/)) {
        if (!line.match(/^\d{4}-\d{2}-\d{2}/)) continue;
        const trimmed = line.trim();
        const parts = trimmed.split(/\s+/);
        // parts: [date, time, attr, size, comp, name...]
        if (parts.length >= 6 && parts[2] && !parts[2].endsWith('D') && !trimmed.endsWith('\\')) {
          const fileName = parts.slice(5).join(' ');
          try {
            execSync(`"${bz}" t -y ${pwdFlag} "${rarFilePath}" "${fileName}"`, { stdio: 'ignore', timeout: 60000 });
            return true; // Data decryption succeeded
          } catch (e) {
            return false; // Wrong password or data error
          }
        }
      }
      // Only directories found or empty — header listing succeeded
      return true;
    } catch (e) {
      return false; // Header decryption failed (wrong password)
    }
  };

  const initialMsg = 'Checking archive encryption (Fast Mode)...';
  if (onProgress) onProgress(initialMsg);
  logger.info(`[Bandizip] ${initialMsg}`);

  if (testFast('')) return '';

  for (const cand of candidates) {
    const msg = `Testing password: "${cand}"`;
    if (onProgress) onProgress(msg);
    logger.info(`[Bandizip] ${msg}`);

    if (testFast(cand)) return cand;
  }

  return '';
}

// Returns the shallowest directory under `root` that directly contains an
// eboot.bin (case-insensitive), or null if none is found. Read-only — it never
// moves, deletes, or otherwise touches the tree; it only reports a path. Handing
// that path to the compressor strips meaningless wrapper folders above the game
// while keeping everything at and below the game's top level intact (encrypted
// files, the decrypted/ subfolder, etc.). Breadth-first so the topmost eboot
// wins, never a deeper duplicate.
function findShallowestEbootDir(root) {
  if (!fs.existsSync(root)) return null;
  const queue = [root];
  while (queue.length > 0) {
    const dir = queue.shift();
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); }
    catch (e) { continue; }
    const subDirs = [];
    for (const ent of entries) {
      if (ent.isDirectory()) {
        subDirs.push(path.join(dir, ent.name));
      } else if (ent.name.toLowerCase() === 'eboot.bin') {
        return dir; // shallowest match wins
      }
    }
    queue.push(...subDirs);
  }
  return null;
}

async function compressFolderTo7z(folderPath, dest7zPath, onProgress) {
  const bz = requireBz();
  const tmpPath = dest7zPath.replace(/\.7z$/i, '.compressing');
  const args = ['a', '-r', '-fmt:7z', '-l:5', '-y', tmpPath, `${folderPath}\\\\*`];

  return new Promise((resolve, reject) => {
    const child = spawn(bz, args, { shell: false });
    let stderrOut = '';

    child.stdout.on('data', (data) => {
      if (onProgress) {
        const text = data.toString().trim();
        const lines = text.split('\n');
        for (const line of lines) {
          if (line.includes('Compressing')) {
            onProgress(line.trim());
          }
        }
      }
    });

    child.stderr.on('data', (data) => {
      stderrOut += data.toString();
    });

    child.on('close', (code) => {
      if (code !== 0) {
        reject(new Error(`bz compression failed: ${stderrOut.trim() || 'Code ' + code}`));
      } else {
        try { fs.renameSync(tmpPath, dest7zPath); } catch (e) {}
        resolve();
      }
    });
  });
}

async function compressFileTo7z(filePath, dest7zPath, onProgress) {
  const bz = requireBz();
  const tmpPath = dest7zPath.replace(/\.7z$/i, '.compressing');
  const args = ['a', '-fmt:7z', '-l:5', '-y', tmpPath, filePath];

  return new Promise((resolve, reject) => {
    const child = spawn(bz, args, { shell: false });
    let stderrOut = '';

    child.stdout.on('data', (data) => {
      if (onProgress) {
        const text = data.toString().trim();
        const lines = text.split('\n');
        for (const line of lines) {
          if (line.includes('Compressing')) {
            onProgress(line.trim());
          }
        }
      }
    });

    child.stderr.on('data', (data) => {
      stderrOut += data.toString();
    });

    child.on('close', (code) => {
      if (code !== 0) {
        reject(new Error(`bz compression failed: ${stderrOut.trim() || 'Code ' + code}`));
      } else {
        try { fs.renameSync(tmpPath, dest7zPath); } catch (e) {}
        resolve();
      }
    });
  });
}

module.exports = {
  archiveContainsExfat,
  isArchiveEncrypted,
  extractRarArchive,
  getGameInfoFromArchive,
  compressFolderTo7z,
  compressFileTo7z,
  findShallowestEbootDir,
  findWorkingPassword,
  findParamJson,
  sanitizeFileName,
  buildCandidates
};