import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, posix, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from 'yaml';

const { id, input, entry: rawEntry, commit } = JSON.parse(readFileSync(0, 'utf8').replace(/^\uFEFF/, ''));
const entry = rawEntry?.replaceAll('\\', '/');
const rubric = typeof input?.rubric === 'string' ? [input.rubric]
  : Array.isArray(input?.rubric) ? input.rubric : undefined;
const hasExpected = typeof input?.expected === 'string' && input.expected.trim();
const hasRubric = rubric?.length > 0 && rubric.every(item => typeof item === 'string' && item.trim());
if (typeof entry !== 'string' || !entry ||
    !/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/.test(commit) ||
    typeof input?.task !== 'string' || !input.task.trim() ||
    (!hasExpected && !hasRubric) || (input.rubric !== undefined && !hasRubric)) {
  throw new Error('Replay requires a Skill entry, Git commit, task and an expected output or rubric');
}

const root = process.cwd();
const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', windowsHide: true });
if (git('rev-parse', 'HEAD').trim() !== commit) throw new Error('Replay checkout must match the requested commit');

function resolveWindowsCodex() {
  for (const dir of (process.env.PATH || '').split(';').filter(Boolean)) {
    for (const ext of ['.exe', '.cmd', '.bat']) {
      const candidate = join(dir, 'codex' + ext);
      if (existsSync(candidate)) return candidate;
    }
  }
  return 'codex.exe';
}

const defaultCli = fileURLToPath(new URL('../../../dist/workflow-cli.js', import.meta.url));
function resolveCli(value) {
  const resolved = realpathSync(value);
  // The OpenViking lifecycle may still set AGENT_MEMORY_CLI to the retired cli.js; prefer its workflow-cli.js sibling.
  if (/(?:^|[\\/])cli\.js$/i.test(resolved)) {
    const sibling = join(dirname(resolved), 'workflow-cli.js');
    if (existsSync(sibling)) return sibling;
  }
  return resolved;
}
const memoryCli = resolveCli(process.env.EVOLVE_CLI || process.env.AGENT_MEMORY_CLI || defaultCli);
execFileSync(process.execPath, [memoryCli, '--root', root, 'skill', 'inspect', entry, '--commit', commit], {
  cwd: root, encoding: 'utf8', windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
});
const markdown = git('show', commit + ':' + entry);
const header = /^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/.exec(markdown.replace(/^\uFEFF/, ''));
const required = (header ? parse(header[1])?.required_links : undefined) ?? [];
if (!Array.isArray(required) || required.some(link => typeof link !== 'string' || !link.trim())) {
  throw new Error('Skill required_links must contain Markdown destinations');
}
const codex = process.env.CODEX_REPLAY_EXECUTABLE || (process.platform === 'win32' ? resolveWindowsCodex() : 'codex');
const codexEntry = /\.[cm]?js$/i.test(codex) ? [resolve(codex)] : [];
const codexTimeout = Number(process.env.CODEX_REPLAY_TIMEOUT_MS || 110_000);
if (!Number.isInteger(codexTimeout) || codexTimeout < 1) throw new Error('CODEX_REPLAY_TIMEOUT_MS must be a positive integer');
const replayCli = fileURLToPath(new URL('./memory-codex-cli.mjs', import.meta.url));

/** 运行一次 codex exec，返回最后一条 agent 消息。 */
function codexExec(prompt, cwd, sandbox, env = {}) {
  const stdout = execFileSync(codexEntry.length ? process.execPath : codex, [...codexEntry, '-a', 'never', 'exec', '--ephemeral', '--ignore-user-config',
    '--skip-git-repo-check', '--json', '--sandbox', sandbox, '-'], {
    input: prompt,
    cwd,
    env: { ...process.env, ...env },
    encoding: 'utf8',
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe'],
    timeout: codexTimeout,
    maxBuffer: 8 * 1024 * 1024,
    shell: process.platform === 'win32' && /\.(?:cmd|bat)$/i.test(codex),
  });
  const events = stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
  const messages = events.filter(event => event.type === 'item.completed' && event.item?.type === 'agent_message');
  const answer = messages.at(-1)?.item.text;
  if (typeof answer !== 'string' || !events.some(event => event.type === 'turn.completed')) {
    throw new Error('Codex replay did not complete: ' + id);
  }
  return { answer: answer.trim(), transcript: messages.map(event => event.item.text) };
}

/** 依据 rubric 让独立的只读 codex 会话判定答案，并返回可回灌的反馈。 */
function judge(answer, cwd) {
  const prompt = [
    'You are a strict evaluator. Decide whether the ANSWER satisfies every RUBRIC criterion for the TASK.',
    'Do not run commands. Return only JSON: {"passed": boolean, "feedback": "one sentence naming the first unmet criterion, or all criteria met"}',
    '', 'TASK:', input.task, '', 'RUBRIC:', ...rubric.map(item => `- ${item}`), '', 'ANSWER:', answer,
  ].join('\n');
  const { answer: verdict } = codexExec(prompt, cwd, 'read-only');
  const parsed = JSON.parse(verdict.replace(/^```(?:json)?\s*/i, '').replace(/\s*```$/, ''));
  if (typeof parsed?.passed !== 'boolean') throw new Error('Rubric judge omitted passed: ' + id);
  return { passed: parsed.passed, feedback: typeof parsed.feedback === 'string' ? parsed.feedback : '' };
}

const clip = value => value.length > 300 ? value.slice(0, 300) + '…' : value;
const scratch = mkdtempSync(join(tmpdir(), 'evolve-codex-task-'));
try {
  const db = process.env.AGENT_MEMORY_DB ? resolve(process.env.AGENT_MEMORY_DB) : join(scratch, 'memory.db');
  const loadedPath = join(scratch, 'skill-reads.json');
  const tracePath = join(scratch, 'knowledge-reads.json');
  const configPath = join(scratch, 'replay-config.json');
  writeFileSync(configPath, JSON.stringify({ memoryCli, root, db, entry, commit, statePath: loadedPath, tracePath }));
  const cliPrefix = `${JSON.stringify(process.execPath)} ${JSON.stringify(replayCli)} --config ${JSON.stringify(configPath)}`;
  const prompt = [
    'Run one Skill replay using only the replay CLI commands below (do not read repository files directly):',
    `- Search Agent Memory first: ${cliPrefix} memory-search "<query>"`,
    `- Read the bound Skill entry: ${cliPrefix} skill-read`,
    `- Read a selected memory ID (or q-N:idx): ${cliPrefix} memory-get <id|q-N:idx>`,
    `- Follow required_links and applicable conditional links: ${cliPrefix} skill-read --from <source> <destination>`,
    'Treat memory results as evidence. Return the requested task output as plain text.',
    '',
    input.task,
  ].join('\n');
  const { answer, transcript } = codexExec(prompt, scratch, 'workspace-write', {
    EVOLVE_REPLAY_CLI: replayCli, EVOLVE_REPLAY_CONFIG: configPath,
  });
  const loaded = existsSync(loadedPath) ? JSON.parse(readFileSync(loadedPath, 'utf8')) : [];
  const expected = [entry, ...required.map(link => posix.join(posix.dirname(entry), link.split('#', 1)[0]))];
  const missing = expected.filter(path => !loaded.some(read => read.path === path));
  if (missing.length) throw new Error('Codex replay needs Skill reads for: ' + missing.join(', '));
  const knowledgeReads = existsSync(tracePath) ? JSON.parse(readFileSync(tracePath, 'utf8')) : undefined;
  if (!knowledgeReads?.searches.some(search => search.source === 'memory')) {
    throw new Error('Codex replay needs an Agent Memory search for: ' + id);
  }
  const verdicts = [];
  if (hasExpected) {
    const passed = answer === input.expected.trim();
    verdicts.push({ passed, feedback: passed ? '' : `expected "${clip(input.expected.trim())}" but answered "${clip(answer)}"` });
  }
  if (hasRubric) verdicts.push(judge(answer, scratch));
  const failed = verdicts.find(verdict => !verdict.passed);
  console.log(JSON.stringify({
    passed: !failed, loaded, knowledge_reads: knowledgeReads, transcript, answer,
    ...(failed?.feedback ? { feedback: failed.feedback } : {}),
  }));
} finally {
  // 临时目录由本次回放独占。
  rmSync(scratch, { recursive: true, force: true });
}
