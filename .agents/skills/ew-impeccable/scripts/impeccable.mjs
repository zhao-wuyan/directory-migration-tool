#!/usr/bin/env node
// Modified for Self-Evolving Workflow. See UPSTREAM.md.
/**
 * Self-Evolving Workflow launcher & adapter for Impeccable 4.9.2 (engine 0.1.25).
 *
 * Locates, verifies, or downloads the pinned platform engine binary (matching
 * upstream scripts/impeccable and cli/bin/cli.js), enforces
 * IMPECCABLE_NO_UPDATE_CHECK=1 by default, and adapts `hooks`, `pin`,
 * `context`, and `doctor` for projects installing `skills/ew-impeccable/`.
 */

import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SCRIPTS_DIR = path.dirname(fileURLToPath(import.meta.url));
const SKILL_DIR = path.dirname(SCRIPTS_DIR);
const VERSION_PATH = path.join(SCRIPTS_DIR, 'VERSION');
const MANIFEST_PATH = path.join(SCRIPTS_DIR, 'engine-checksums.txt');
const METADATA_PATH = path.join(SCRIPTS_DIR, 'command-metadata.json');

const OS_NAME = ({ darwin: 'darwin', linux: 'linux', win32: 'windows' }[process.platform]) || process.platform;
const ARCH_NAME = ({ arm64: 'arm64', x64: 'x64' }[process.arch]) || process.arch;
const TARGET = `${OS_NAME}-${ARCH_NAME}`;
const EXE = OS_NAME === 'windows' ? '.exe' : '';
const ASSET = `impeccable-${TARGET}${EXE}`;
const VERSION = fs.existsSync(VERSION_PATH) ? fs.readFileSync(VERSION_PATH, 'utf8').trim() : '';

const EW_HOOK_MARKERS = [
  'skills/ew-impeccable/scripts/impeccable',
  'skills/ew-impeccable/scripts/hook.mjs',
  'skills/ew-impeccable/scripts/hook-before-edit.mjs',
  'skills/impeccable/scripts/impeccable',
  'skills/impeccable/scripts/hook.mjs',
  'skills/impeccable/scripts/hook-before-edit.mjs',
  'skills/impeccable/scripts/hook-after-edit.mjs',
  'skills/impeccable/scripts/hook-stop.mjs',
  'skills/impeccable/scripts/hook-probe.mjs',
  'workflows/impeccable/scripts/hook.mjs',
];

const VALID_PIN_COMMANDS = new Set([
  'init', 'extract', 'document', 'shape', 'critique', 'audit', 'polish',
  'bolder', 'quieter', 'distill', 'harden', 'onboard', 'live', 'animate', 'colorize',
  'typeset', 'layout', 'delight', 'overdrive', 'clarify', 'adapt', 'optimize', 'generate',
  'hooks', 'doctor', 'foundations', 'teach',
]);
const DEPRECATED_PIN_COMMANDS = new Set(['craft']);

const PIN_MARKER = '<!-- impeccable-pinned-skill -->';
const OPENCODE_PIN_MARKER = '<!-- impeccable-pinned-command -->';

function isFile(filePath) {
  try {
    return !!filePath && fs.statSync(filePath).isFile();
  } catch {
    return false;
  }
}

function readJsonSafe(filePath) {
  if (!isFile(filePath)) return null;
  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  } catch {
    return null;
  }
}

function normalizeSeparators(command) {
  return String(command).replaceAll('\\', '/');
}

function isHookCommand(command) {
  if (typeof command !== 'string') return false;
  const norm = normalizeSeparators(command);
  return EW_HOOK_MARKERS.some((marker) => norm.includes(marker));
}

function valueHasHookMarker(value) {
  if (typeof value === 'string') return isHookCommand(value);
  if (Array.isArray(value)) return value.some(valueHasHookMarker);
  if (value && typeof value === 'object') return Object.values(value).some(valueHasHookMarker);
  return false;
}

function expectedDigest(wantedAsset = ASSET) {
  if (!VERSION || !isFile(MANIFEST_PATH)) return '';
  for (const line of fs.readFileSync(MANIFEST_PATH, 'utf8').split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;
    const [ver, asset, sum] = trimmed.split(/\s+/);
    if (ver === VERSION && asset === wantedAsset && sum) return sum.toLowerCase();
  }
  return '';
}

function acceptedDigests() {
  const list = [expectedDigest()];
  if (OS_NAME === 'windows' && ARCH_NAME === 'arm64') {
    list.push(expectedDigest('impeccable-windows-x64.exe'));
  }
  return list.filter(Boolean);
}

function stampValid(binPath, stampPath, accepted) {
  if (!isFile(stampPath) || accepted.length === 0) return false;
  try {
    const [digest, size] = fs.readFileSync(stampPath, 'utf8').trim().split(/\s+/);
    const binStat = fs.statSync(binPath);
    const stampStat = fs.statSync(stampPath);
    return accepted.includes(digest?.toLowerCase()) && size === String(binStat.size) && binStat.mtimeMs <= stampStat.mtimeMs;
  } catch {
    return false;
  }
}

function sha256File(filePath) {
  return createHash('sha256').update(fs.readFileSync(filePath)).digest('hex').toLowerCase();
}

function probeEngine(binPath) {
  const res = spawnSync(binPath, ['engine-probe'], {
    env: { ...process.env, IMPECCABLE_LAUNCHER_PROBE: '1' },
    encoding: 'utf8',
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'ignore'],
  });
  if (res.status !== 0 || typeof res.stdout !== 'string') return null;
  const line = res.stdout.trim();
  if (!line.startsWith('impeccable-engine')) return null;
  return line.slice('impeccable-engine'.length).trim();
}

function findOnPath() {
  const pathEnv = process.env.PATH || '';
  const exts = OS_NAME === 'windows' ? ['.exe', '.com', '.cmd', '.bat'] : [''];
  for (const dir of pathEnv.split(path.delimiter).filter(Boolean)) {
    for (const ext of exts) {
      const candidate = path.join(dir, `impeccable${ext}`);
      if (!isFile(candidate)) continue;
      if (probeEngine(candidate) !== null) return candidate;
    }
  }
  return null;
}

async function downloadBinary(cachedPath) {
  if (process.env.IMPECCABLE_LAUNCHER_PROBE) return null;
  const base = (process.env.IMPECCABLE_DOWNLOAD_BASE || 'https://github.com/pbakaus/impeccable/releases/download').replace(/\/$/, '');
  const candidates = [];
  const primary = expectedDigest();
  if (primary) candidates.push({ asset: ASSET, expected: primary });
  if (OS_NAME === 'windows' && ARCH_NAME === 'arm64') {
    const x64 = expectedDigest('impeccable-windows-x64.exe');
    if (x64) candidates.push({ asset: 'impeccable-windows-x64.exe', expected: x64 });
  }
  if (!VERSION || candidates.length === 0) {
    throw new Error(`no checksum for engine ${VERSION} (${ASSET}) in ${MANIFEST_PATH}; refusing the unverified download`);
  }

  let lastErr = null;
  for (const { asset, expected } of candidates) {
    const url = `${base}/engine-v${VERSION}/${asset}`;
    try {
      const res = await fetch(url, { redirect: 'follow' });
      if (!res.ok) {
        lastErr = new Error(`HTTP ${res.status} fetching ${url}`);
        continue;
      }
      const buf = Buffer.from(await res.arrayBuffer());
      if (buf.length === 0) {
        lastErr = new Error(`downloaded file is empty: ${url}`);
        continue;
      }
      const actual = createHash('sha256').update(buf).digest('hex').toLowerCase();
      if (actual !== expected) {
        throw new Error(`checksum mismatch downloading ${url}`);
      }
      const cacheDir = path.dirname(cachedPath);
      fs.mkdirSync(cacheDir, { recursive: true });
      const tmp = path.join(cacheDir, `.impeccable.part.${process.pid}${EXE}`);
      try {
        fs.writeFileSync(tmp, buf, { mode: 0o755 });
        fs.renameSync(tmp, cachedPath);
      } catch (err) {
        try { fs.rmSync(tmp, { force: true }); } catch { /* ignore */ }
        throw err;
      }
      try {
        fs.writeFileSync(`${cachedPath}.verified`, `${actual} ${buf.length}\n`);
      } catch { /* next run hashes again */ }
      return cachedPath;
    } catch (err) {
      lastErr = err;
    }
  }
  if (lastErr) throw lastErr;
  return null;
}

async function resolveBinary() {
  const envBin = process.env.IMPECCABLE_BIN;
  if (isFile(envBin)) return envBin;

  const siblingBin = path.join(SCRIPTS_DIR, 'bin', TARGET, `impeccable${EXE}`);
  if (isFile(siblingBin)) {
    const pv = probeEngine(siblingBin);
    if (!VERSION || pv === VERSION) return siblingBin;
  }

  const homeBin = path.join(os.homedir(), '.impeccable', 'bin', `impeccable${EXE}`);
  if (isFile(homeBin) && probeEngine(homeBin) !== null) return homeBin;

  const cacheRoot = process.env.IMPECCABLE_HOME || path.join(os.homedir(), '.impeccable');
  const cached = path.join(cacheRoot, 'bin', VERSION, `impeccable${EXE}`);
  const accepted = acceptedDigests();
  if (VERSION && isFile(cached) && accepted.length > 0) {
    const stamp = `${cached}.verified`;
    if (stampValid(cached, stamp, accepted)) return cached;
    const digest = sha256File(cached);
    if (accepted.includes(digest)) {
      try {
        fs.writeFileSync(stamp, `${digest} ${fs.statSync(cached).size}\n`);
      } catch { /* ignore */ }
      return cached;
    }
    process.stderr.write(`impeccable: cannot verify cached engine ${cached} against ${MANIFEST_PATH}; downloading engine ${VERSION} again\n`);
  }

  const pathBin = findOnPath();
  if (pathBin) return pathBin;

  return downloadBinary(cached);
}

function buildChildEnv() {
  const selfScript = path.join(SCRIPTS_DIR, 'impeccable.mjs').replaceAll('\\', '/');
  return {
    ...process.env,
    IMPECCABLE_SKILL_DIR: process.env.IMPECCABLE_SKILL_DIR || SKILL_DIR,
    IMPECCABLE_NO_UPDATE_CHECK: process.env.IMPECCABLE_NO_UPDATE_CHECK || '1',
    IMPECCABLE_SELF: process.env.IMPECCABLE_SELF || `node "${selfScript}"`,
  };
}

function hasStandardImpeccableSkill(cwd) {
  const roots = ['.claude', '.agents', '.cursor', '.github', '.grok', '.gemini'];
  return roots.some((dir) => fs.existsSync(path.join(cwd, dir, 'skills', 'impeccable')));
}

function windowsHookCommand(relLauncher, verb) {
  const shim = `"${relLauncher.replaceAll('/', '\\')}.cmd"`;
  return `cmd /c if exist ${shim} ${shim} ${verb}`;
}

function ewHookTargets(cwd) {
  const relLauncher = 'skills/ew-impeccable/scripts/impeccable';
  const claudeCmd = `"\${CLAUDE_PROJECT_DIR}/${relLauncher}" hook`;
  const localCmd = `"${relLauncher}" hook`;
  const winCmd = windowsHookCommand(relLauncher, 'hook');
  const githubCmd = `"$(git rev-parse --show-toplevel)/${relLauncher}" hook`;

  const stopEntry = (cmd, win) => ({
    hooks: [
      win
        ? { type: 'command', command: cmd, commandWindows: win, timeout: 30, statusMessage: 'Design deep pass' }
        : { type: 'command', command: cmd, timeout: 30, statusMessage: 'Design deep pass' },
    ],
  });

  const postEntry = (matcher, cmd, win) => ({
    matcher,
    hooks: [
      win
        ? { type: 'command', command: cmd, commandWindows: win, timeout: 5, statusMessage: 'Checking UI changes' }
        : { type: 'command', command: cmd, timeout: 5, statusMessage: 'Checking UI changes' },
    ],
  });

  return [
    {
      provider: '.claude',
      present: fs.existsSync(path.join(cwd, '.claude')),
      destRel: '.claude/settings.local.json',
      sharedDestRel: '.claude/settings.json',
      manifest: () => ({
        description: 'Impeccable design detector: immediate-tier checks after Edit/Write on UI files, full-rule deep pass on Stop.',
        hooks: {
          PostToolUse: [postEntry('Edit|Write', claudeCmd)],
          Stop: [stopEntry(claudeCmd)],
        },
      }),
    },
    {
      provider: '.codex',
      present: fs.existsSync(path.join(cwd, '.codex')),
      destRel: '.codex/hooks.json',
      manifest: () => ({
        hooks: {
          PostToolUse: [postEntry('Edit|Write|apply_patch', localCmd, winCmd)],
          Stop: [stopEntry(localCmd, winCmd)],
        },
      }),
    },
    {
      provider: '.agents',
      present: fs.existsSync(path.join(cwd, '.agents')),
      destRel: '.agents/hooks.json',
      manifest: () => ({
        hooks: {
          PostToolUse: [postEntry('Edit|Write|apply_patch', localCmd, winCmd)],
          Stop: [stopEntry(localCmd, winCmd)],
        },
      }),
    },
    {
      provider: '.cursor',
      present: fs.existsSync(path.join(cwd, '.cursor')),
      destRel: '.cursor/hooks.json',
      manifest: () => ({
        version: 1,
        hooks: {
          postToolUse: [
            {
              command: process.platform === 'win32' ? winCmd : localCmd,
              matcher: '^Write$',
              timeout: 5,
            },
          ],
        },
      }),
    },
    {
      provider: '.github',
      present: fs.existsSync(path.join(cwd, '.github')),
      destRel: '.github/hooks/impeccable.json',
      manifest: () => ({
        version: 1,
        hooks: {
          postToolUse: [
            {
              type: 'command',
              matcher: 'edit|create|apply_patch',
              bash: githubCmd,
              timeoutSec: 5,
            },
          ],
        },
      }),
    },
  ];
}

function stripHookEntry(entry) {
  if (!entry || typeof entry !== 'object') return entry;
  if (
    valueHasHookMarker(entry.command)
    || valueHasHookMarker(entry.commandWindows)
    || valueHasHookMarker(entry.args)
    || valueHasHookMarker(entry.bash)
    || valueHasHookMarker(entry.powershell)
  ) {
    return null;
  }
  if (!Array.isArray(entry.hooks)) return entry;
  const kept = entry.hooks.map(stripHookEntry).filter(Boolean);
  if (kept.length === 0 && entry.hooks.some(valueHasHookMarker)) return null;
  return { ...entry, hooks: kept };
}

function mergeHookManifests(existing, fresh) {
  const existingObj = existing && typeof existing === 'object' && !Array.isArray(existing) ? existing : {};
  const existingHooks = existingObj.hooks && typeof existingObj.hooks === 'object' ? existingObj.hooks : {};
  const freshHooks = fresh.hooks || {};
  const merged = { ...existingObj };
  if (fresh.version !== undefined) merged.version = fresh.version;
  if (fresh.description !== undefined) merged.description = fresh.description;

  const events = Array.from(new Set([...Object.keys(existingHooks), ...Object.keys(freshHooks)]));
  const hooks = {};
  for (const event of events) {
    const preserved = Array.isArray(existingHooks[event])
      ? existingHooks[event].map(stripHookEntry).filter(Boolean)
      : [];
    const added = Array.isArray(freshHooks[event]) ? freshHooks[event] : [];
    const combined = [...preserved, ...added];
    if (combined.length > 0) hooks[event] = combined;
  }
  merged.hooks = hooks;
  return merged;
}

function pruneHookManifest(filePath) {
  if (!isFile(filePath)) return false;
  const parsed = readJsonSafe(filePath);
  if (!parsed || typeof parsed !== 'object' || !parsed.hooks || !valueHasHookMarker(parsed.hooks)) return false;

  const cleanedHooks = {};
  for (const [event, entries] of Object.entries(parsed.hooks)) {
    const kept = Array.isArray(entries) ? entries.map(stripHookEntry).filter(Boolean) : [];
    if (kept.length > 0) cleanedHooks[event] = kept;
  }
  const next = { ...parsed };
  if (Object.keys(cleanedHooks).length > 0) {
    next.hooks = cleanedHooks;
  } else {
    delete next.hooks;
    delete next.description;
    delete next.version;
  }
  if (Object.keys(next).length === 0) {
    fs.rmSync(filePath, { force: true });
  } else {
    fs.writeFileSync(filePath, `${JSON.stringify(next, null, 2)}\n`);
  }
  return true;
}

function repairEwHookManifests(cwd) {
  const result = { written: [], already: [], backups: [] };
  for (const target of ewHookTargets(cwd)) {
    if (!target.present) continue;
    const dest = path.join(cwd, target.destRel);
    const sharedDest = target.sharedDestRel ? path.join(cwd, target.sharedDestRel) : null;
    if (sharedDest && valueHasHookMarker(readJsonSafe(sharedDest)?.hooks)) {
      pruneHookManifest(dest);
      result.already.push(target.provider);
      continue;
    }
    const fresh = target.manifest();
    let next = fresh;
    if (isFile(dest)) {
      const parsed = readJsonSafe(dest);
      if (parsed) {
        next = mergeHookManifests(parsed, fresh);
      } else {
        const backup = `${dest}.bak`;
        fs.copyFileSync(dest, backup);
        result.backups.push(path.relative(cwd, backup) || backup);
      }
    }
    const serialized = `${JSON.stringify(next, null, 2)}\n`;
    const current = isFile(dest) ? fs.readFileSync(dest, 'utf8') : null;
    if (current === serialized) {
      result.already.push(target.provider);
      continue;
    }
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    fs.writeFileSync(dest, serialized);
    result.written.push(target.provider);
  }
  return result;
}

function findWiredEwProviders(cwd) {
  const wired = [];
  for (const target of ewHookTargets(cwd)) {
    const dest = path.join(cwd, target.destRel);
    const shared = target.sharedDestRel ? path.join(cwd, target.sharedDestRel) : null;
    if (valueHasHookMarker(readJsonSafe(dest)?.hooks) || (shared && valueHasHookMarker(readJsonSafe(shared)?.hooks))) {
      wired.push(target.provider);
    }
  }
  return wired;
}

function handleHooksVerb(binPath, env, args) {
  const cwd = process.cwd();
  const action = (args[0] || 'status').toLowerCase();
  if (hasStandardImpeccableSkill(cwd) || !fs.existsSync(path.join(cwd, 'skills', 'ew-impeccable'))) {
    const res = spawnSync(binPath, ['hooks', ...args], { env, stdio: 'inherit', windowsHide: true });
    return res.status ?? 1;
  }

  const res = spawnSync(binPath, ['hooks', ...args], { env, encoding: 'utf8', windowsHide: true, stdio: ['inherit', 'pipe', 'pipe'] });
  let stdout = res.stdout || '';
  const stderr = res.stderr || '';
  if (stderr) process.stderr.write(stderr);
  if ((res.status ?? 1) !== 0) {
    if (stdout) process.stdout.write(stdout);
    return res.status ?? 1;
  }

  if (action === 'on') {
    const repaired = repairEwHookManifests(cwd);
    const anyWired = repaired.written.length > 0 || repaired.already.length > 0;
    if (anyWired) {
      stdout = stdout
        .replace(/\s*No installed provider skill folders found to repair\.?/g, '')
        .replace(/\s*No design harness is wired\.[^\n]*/g, '')
        .trimEnd();
      const notes = [];
      if (repaired.written.length > 0) notes.push(`Installed or repaired EW hook manifests for: ${repaired.written.join(', ')}.`);
      if (repaired.already.length > 0) notes.push(`EW hook manifests already installed for: ${repaired.already.join(', ')}.`);
      if (repaired.backups.length > 0) notes.push(`Backed up malformed manifest(s): ${repaired.backups.join(', ')}.`);
      stdout = `${stdout} ${notes.join(' ')}\n`;
    }
  } else if (action === 'reset') {
    const pruned = [];
    for (const target of ewHookTargets(cwd)) {
      if (pruneHookManifest(path.join(cwd, target.destRel))) pruned.push(target.provider);
    }
    if (pruned.length > 0) {
      stdout = `${stdout.trimEnd()} Removed EW hook entries from: ${pruned.join(', ')}.\n`;
    }
  } else if (action === 'status') {
    const wired = findWiredEwProviders(cwd);
    if (wired.length > 0) {
      stdout = stdout.replace(/\s*enabled in config, not wired in any harness;[^\n]*/g, '').trimEnd();
      for (const provider of wired) {
        stdout = stdout.replace(
          new RegExp(`(  wiring ${provider.replace('.', '\\.')}: )not wired \\(missing or stale entries\\)`),
          '$1wired (ew-impeccable)',
        );
        if (!stdout.includes(`wiring ${provider}:`)) {
          stdout += `\n  wiring ${provider}: wired (ew-impeccable)`;
        }
      }
      stdout += '\n';
    }
  }

  if (stdout) process.stdout.write(stdout);
  return 0;
}

function handleContextVerb(binPath, env, args) {
  const cwd = process.cwd();
  const res = spawnSync(binPath, ['context', ...args], { env, encoding: 'utf8', windowsHide: true, stdio: ['inherit', 'pipe', 'pipe'] });
  let stdout = res.stdout || '';
  if (res.stderr) process.stderr.write(res.stderr);

  if ((res.status ?? 1) === 0 && !hasStandardImpeccableSkill(cwd) && fs.existsSync(path.join(cwd, 'skills', 'ew-impeccable'))) {
    const shared = readJsonSafe(path.join(cwd, '.impeccable', 'config.json'));
    const local = readJsonSafe(path.join(cwd, '.impeccable', 'config.local.json'));
    const enabled = shared?.hook?.enabled !== false && local?.hook?.enabled !== false;
    const consented = local?.hook?.consent === 'accepted';
    if (enabled && consented) {
      const repaired = repairEwHookManifests(cwd);
      if (repaired.written.length > 0 && !stdout.includes('Repaired Impeccable hook wiring')) {
        stdout = `${stdout.trimEnd()}\nRepaired Impeccable hook wiring for: ${repaired.written.join(', ')}.\n`;
      }
    }
    if (enabled && findWiredEwProviders(cwd).length > 0) {
      stdout = stdout
        .split(/\r?\n/)
        .filter((line) => !line.startsWith('MANUAL_DETECTOR_REQUIRED:'))
        .join('\n');
      if (stdout && !stdout.endsWith('\n')) stdout += '\n';
    }
  }

  if (stdout) process.stdout.write(stdout);
  return res.status ?? 1;
}

function handleDoctorVerb(binPath, env, args) {
  const cwd = process.cwd();
  const res = spawnSync(binPath, ['doctor', ...args], { env, encoding: 'utf8', windowsHide: true, stdio: ['inherit', 'pipe', 'pipe'] });
  if (res.stderr) process.stderr.write(res.stderr);
  const stdout = res.stdout || '';

  if (
    (res.status ?? 1) === 0
    && args.includes('--json')
    && !hasStandardImpeccableSkill(cwd)
    && fs.existsSync(path.join(cwd, 'skills', 'ew-impeccable'))
    && findWiredEwProviders(cwd).length > 0
  ) {
    try {
      const parsed = JSON.parse(stdout);
      if (Array.isArray(parsed.findings)) {
        parsed.findings = parsed.findings.filter((f) => !(f.id === 'hook-not-wired' || f.id === 'hook-missing'));
      }
      process.stdout.write(`${JSON.stringify(parsed, null, 2)}\n`);
      return 0;
    } catch { /* fall through */ }
  }

  if (stdout) process.stdout.write(stdout);
  return res.status ?? 1;
}

function findProjectRoot(start) {
  let dir = path.resolve(start);
  while (true) {
    if (fs.existsSync(path.join(dir, 'package.json')) || fs.existsSync(path.join(dir, '.git')) || fs.existsSync(path.join(dir, 'skills-lock.json'))) {
      return dir;
    }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return path.resolve(start);
}

function handlePinVerb(binPath, env, args) {
  const cwd = process.cwd();
  if (hasStandardImpeccableSkill(cwd)) {
    const res = spawnSync(binPath, ['pin', ...args], { env, stdio: 'inherit', windowsHide: true });
    return res.status ?? 1;
  }

  const [action, command] = args;
  if (!action || !command || !['pin', 'unpin'].includes(action)) {
    process.stdout.write('Usage: impeccable pin <pin|unpin> <command>\n');
    process.stdout.write(`\nAvailable commands: ${Array.from(VALID_PIN_COMMANDS).join(', ')}\n`);
    return 1;
  }
  if (action === 'pin' && DEPRECATED_PIN_COMMANDS.has(command)) {
    process.stderr.write(`Command '${command}' is deprecated; use natural new-work requests or /ew-impeccable shape / init instead (use 'pin unpin ${command}' to remove an existing shortcut).\n`);
    return 1;
  }
  if (!VALID_PIN_COMMANDS.has(command) && !(action === 'unpin' && DEPRECATED_PIN_COMMANDS.has(command))) {
    process.stderr.write(`Unknown command: ${command}\nValid commands: ${Array.from(VALID_PIN_COMMANDS).join(', ')}\n`);
    return 1;
  }

  const metadata = readJsonSafe(METADATA_PATH) || {};
  const entry = metadata[command] || {};
  const desc = String(entry.description || `Shortcut for /ew-impeccable ${command}.`).replaceAll('"', '\\"');
  const hint = String(entry.argumentHint ?? '[target]').replaceAll('"', '\\"');
  const root = findProjectRoot(cwd);

  const harnessNames = ['.claude', '.cursor', '.codex', '.agents', '.agent', '.github', '.grok', '.gemini', '.opencode'];
  const skillDirs = [];
  for (const h of harnessNames) {
    if (fs.existsSync(path.join(root, h))) {
      skillDirs.push(path.join(root, h, 'skills'));
    }
  }
  if (skillDirs.length === 0) {
    skillDirs.push(path.join(os.homedir(), '.claude', 'skills'));
  }

  if (action === 'pin') {
    let created = 0;
    for (const dir of skillDirs) {
      const harness = path.basename(path.dirname(dir));
      const isCodex = harness === '.codex' || harness === '.agents';
      const prefix = isCodex ? '$' : '/';
      const fm = isCodex
        ? `disable-model-invocation: true\nmetadata:\n  argument-hint: "${hint}"`
        : `argument-hint: "${hint}"\nuser-invocable: true\ndisable-model-invocation: true`;
      const targetDir = path.join(dir, command);
      const skillMd = path.join(targetDir, 'SKILL.md');
      if (isFile(skillMd) && !fs.readFileSync(skillMd, 'utf8').includes(PIN_MARKER)) {
        process.stdout.write(`  SKIP: ${targetDir} (non-pinned skill already exists)\n`);
        continue;
      }
      fs.mkdirSync(targetDir, { recursive: true });
      fs.writeFileSync(
        skillMd,
        `---\nname: ${command}\ndescription: "${desc}"\n${fm}\n---\n\n${PIN_MARKER}\n\nThis is a pinned shortcut for \`${prefix}ew-impeccable ${command}\` (explicit user invocation only; do not auto-select or preempt \`ew-impeccable\`).\n\nInvoke ${prefix}ew-impeccable ${command}, passing along any arguments provided here, and follow its instructions.\n`,
      );
      process.stdout.write(`  + ${targetDir}\n`);
      created++;
    }
    if (fs.existsSync(path.join(root, '.opencode'))) {
      const cmdDir = path.join(root, '.opencode', 'commands');
      const cmdFile = path.join(cmdDir, `impeccable-${command}.md`);
      const refDoc = command === 'teach' ? 'init' : command;
      if (!isFile(cmdFile) || fs.readFileSync(cmdFile, 'utf8').includes(OPENCODE_PIN_MARKER)) {
        fs.mkdirSync(cmdDir, { recursive: true });
        fs.writeFileSync(
          cmdFile,
          `---\ndescription: "${desc}"\nagent: build\nsubtask: true\n---\n\n${OPENCODE_PIN_MARKER}\n\nExplicit user-invoked shortcut only; do not auto-select or preempt \`ew-impeccable\`.\n\nLoad \`ew-impeccable\` (at \`skills/ew-impeccable/SKILL.md\`), run \`node <skill-base-dir>/scripts/impeccable.mjs context\`, then load \`<skill-base-dir>/reference/${refDoc}.md\` and follow it.\n\n$ARGUMENTS\n`,
        );
        process.stdout.write(`  + ${cmdFile}\n`);
        created++;
      }
    }
    process.stdout.write(created > 0 ? `\nPinned /${command}.\n` : `\nNo directories updated for /${command}.\n`);
    return 0;
  }

  let removed = 0;
  for (const dir of skillDirs) {
    const targetDir = path.join(dir, command);
    const skillMd = path.join(targetDir, 'SKILL.md');
    if (!isFile(skillMd)) continue;
    if (!fs.readFileSync(skillMd, 'utf8').includes(PIN_MARKER)) {
      process.stdout.write(`  SKIP: ${targetDir} (not a pinned skill)\n`);
      continue;
    }
    fs.rmSync(targetDir, { recursive: true, force: true });
    process.stdout.write(`  - ${targetDir}\n`);
    removed++;
  }
  const opencodeFile = path.join(root, '.opencode', 'commands', `impeccable-${command}.md`);
  if (isFile(opencodeFile) && fs.readFileSync(opencodeFile, 'utf8').includes(OPENCODE_PIN_MARKER)) {
    fs.rmSync(opencodeFile, { force: true });
    process.stdout.write(`  - ${opencodeFile}\n`);
    removed++;
  }
  process.stdout.write(removed > 0 ? `\nUnpinned /${command}.\n` : `\nNo pinned /${command} shortcuts found.\n`);
  return 0;
}

async function main() {
  let binPath;
  try {
    binPath = await resolveBinary();
  } catch (err) {
    process.stderr.write(`impeccable: ${err.message}\n`);
    process.exit(127);
  }
  if (!binPath) {
    process.stderr.write(`impeccable: no engine binary found for ${TARGET} (expected engine ${VERSION}). Set IMPECCABLE_BIN or ensure network access.\n`);
    process.exit(127);
  }

  const args = process.argv.slice(2);
  const env = buildChildEnv();
  const [verb, ...rest] = args;

  if (verb === 'hooks' || verb === 'hook-admin') {
    process.exit(handleHooksVerb(binPath, env, rest));
  }
  if (verb === 'context') {
    process.exit(handleContextVerb(binPath, env, rest));
  }
  if (verb === 'doctor') {
    process.exit(handleDoctorVerb(binPath, env, rest));
  }
  if (verb === 'pin') {
    process.exit(handlePinVerb(binPath, env, rest));
  }

  const res = spawnSync(binPath, args, { env, stdio: 'inherit', windowsHide: true });
  if (res.error) {
    process.stderr.write(`impeccable: failed to execute ${binPath}: ${res.error.message}\n`);
    process.exit(127);
  }
  process.exit(res.status ?? 1);
}

main();
