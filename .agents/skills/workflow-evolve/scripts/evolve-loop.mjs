import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = process.cwd();
const args = process.argv.slice(2);

function option(name, fallback) {
  const index = args.indexOf(name);
  return index < 0 ? fallback : args[index + 1];
}

function has(name) { return args.includes(name); }

function required(name) {
  const value = option(name);
  if (!value || value.startsWith('--')) throw new Error(`Missing ${name}`);
  return value;
}

const iterations = Number(required('--iterations'));
if (!Number.isInteger(iterations) || iterations < 1) throw new Error('--iterations must be a positive integer');
const entry = resolve(root, required('--skill'));
const scenariosPath = resolve(root, required('--scenarios'));
const runner = resolve(root, option('--runner', fileURLToPath(new URL('./codex-memory-replay.mjs', import.meta.url))));
const defaultCli = fileURLToPath(new URL('../../../dist/workflow-cli.js', import.meta.url));
// Prefer EVOLVE_CLI. The OpenViking lifecycle may still set AGENT_MEMORY_CLI to the retired cli.js;
// use the workflow-cli.js sibling so workflow commands and replay resolve the current CLI.
const selectedCli = resolve(root, option('--memory-cli', process.env.EVOLVE_CLI || process.env.AGENT_MEMORY_CLI || defaultCli));
const memoryCli = /(?:^|[\\/])cli\.js$/i.test(selectedCli) && existsSync(join(dirname(selectedCli), 'workflow-cli.js'))
  ? join(dirname(selectedCli), 'workflow-cli.js') : selectedCli;
const agy = option('--agy', process.env.AGY_EXECUTABLE || 'agy');
const maxRetries = Number(option('--max-retries', '1'));
if (!Number.isInteger(maxRetries) || maxRetries < 0) throw new Error('--max-retries must be a non-negative integer');
const historyFlags = ['--session', '--execution', '--history', '--review'].filter(name => has(name));
if (historyFlags.length > 1) throw new Error('Use only one of --session, --execution, --history or --review');
if (!historyFlags.length && !has('--allow-empty-history')) {
  throw new Error('Provide --session, --execution, --history or --review. Use --allow-empty-history only for an explicitly history-free fixture.');
}
for (const flag of historyFlags) required(flag);
if (has('--execution') && (!option('--project') || option('--project').startsWith('--'))) {
  throw new Error('--execution requires --project <source-project-id>');
}
if (!existsSync(entry)) throw new Error(`Skill entry not found: ${entry}`);
if (!existsSync(scenariosPath)) throw new Error(`Scenario file not found: ${scenariosPath}`);
if (!existsSync(runner)) throw new Error(`Replay runner not found: ${runner}`);
if (!existsSync(memoryCli)) throw new Error(`Agent Memory CLI not found: ${memoryCli}`);
process.env.AGENT_MEMORY_CLI = memoryCli;
if (process.env.AGENT_MEMORY_DB) process.env.AGENT_MEMORY_DB = resolve(root, process.env.AGENT_MEMORY_DB);

const skillEntry = entry.replaceAll('\\', '/').replace(`${root.replaceAll('\\', '/')}/`, '');
const packageDir = dirname(skillEntry).replaceAll('\\', '/');
jsonCommand(process.execPath, [memoryCli, '--root', root, 'skill', 'inspect', skillEntry, '--commit', git(['rev-parse', 'HEAD'])]);
const scenarios = JSON.parse(readFileSync(scenariosPath, 'utf8'));
if (!Array.isArray(scenarios) || scenarios.length < 2 ||
    !scenarios.some(item => item.baseline === false) || !scenarios.some(item => item.baseline === true)) {
  throw new Error('--scenarios must contain at least one baseline failure and one baseline pass');
}

// 回归银行：跨运行持久化已验证通过的场景与已处理的信号，默认位于本地 .evolve 目录。
const bankPath = resolve(root, option('--bank', join('.evolve', 'evolution', 'bank', `${basename(packageDir)}.json`)));
const bank = existsSync(bankPath) ? JSON.parse(readFileSync(bankPath, 'utf8'))
  : { schema_version: 'workflow-evolve-bank/1.0', skill: skillEntry, scenarios: [], addressed_signals: [] };
if (bank.schema_version !== 'workflow-evolve-bank/1.0' || bank.skill !== skillEntry ||
    !Array.isArray(bank.scenarios) || !Array.isArray(bank.addressed_signals)) {
  throw new Error(`Scenario bank does not belong to ${skillEntry}: ${bankPath}`);
}
const provided = new Set(scenarios.map(scenario => scenario.id));
let scenarioSet = [
  ...scenarios.map(scenario => ({ ...scenario })),
  ...bank.scenarios.filter(scenario => !provided.has(scenario.id)).map(scenario => ({ ...scenario, baseline: true })),
];
for (const scenario of scenarioSet) {
  if (scenario.samples !== undefined && (!Number.isInteger(scenario.samples) || scenario.samples < 1 || scenario.samples > 10)) {
    throw new Error(`Scenario samples must be an integer from 1 to 10: ${scenario.id}`);
  }
}
const addressedSignals = new Set(bank.addressed_signals.map(item => item.id));

const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
const outputDir = resolve(root, option('--output-dir', join('.evolve', 'evolution', timestamp)));
mkdirSync(outputDir, { recursive: true });
const statePath = join(outputDir, 'state.json');
const allowEmptyHistory = has('--allow-empty-history');
const state = {
  schema_version: 'workflow-evolve/1.0', target: skillEntry, iterations_requested: iterations,
  max_retries: maxRetries, auto: has('--auto'), allow_empty_history: allowEmptyHistory,
  bank: bankPath, bank_scenarios: scenarioSet.length - scenarios.length, rounds: [], status: 'running',
};

function run(command, commandArgs, options = {}) {
  const output = execFileSync(command, commandArgs, {
    cwd: options.cwd ?? root, env: { ...process.env, ...options.env }, encoding: 'utf8', windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe'], input: options.input, timeout: options.timeout ?? 180_000,
  });
  return output.trim();
}

function jsonCommand(command, commandArgs, options = {}) {
  const output = run(command, commandArgs, options);
  try { return JSON.parse(output); } catch (error) { throw new Error(`Expected JSON from ${command}: ${output.slice(-2000)}`, { cause: error }); }
}

function evolve(...commandArgs) { return jsonCommand(process.execPath, [memoryCli, '--root', root, ...commandArgs]); }

function failureText(error) {
  return String(error?.stderr || error?.message || error).trim().slice(-2000);
}

function git(commandArgs) { return run('git', commandArgs); }

/** 运行 scenario.samples 次；pass^k 要求全部样本通过。 */
function runReplay(scenario, commit) {
  const samples = scenario.samples ?? 1;
  const timeout = Number(process.env.EVOLVE_REPLAY_TIMEOUT_MS || 180_000);
  const runs = Array.from({ length: samples }, (_, index) => JSON.parse(run(process.execPath, [runner], {
    input: JSON.stringify({ id: `${scenario.id}-round-${state.rounds.length + 1}${samples > 1 ? `-sample-${index + 1}` : ''}`,
      input: scenario.input, entry: skillEntry, commit }),
    timeout,
  })));
  if (runs.length === 1) return runs[0];
  const failed = runs.find(item => !item.passed);
  return {
    ...runs[0], passed: !failed, ...(failed?.feedback ? { feedback: failed.feedback } : {}),
    samples: runs.map(item => ({ passed: item.passed, ...(item.feedback ? { feedback: item.feedback } : {}) })),
  };
}

function parseAgyResponse(output) {
  const clean = output.trim().replace(/^```json\s*/i, '').replace(/\s*```$/, '');
  const parsed = JSON.parse(clean);
  const text = typeof parsed?.response === 'string' ? parsed.response
    : typeof parsed?.result === 'string' ? parsed.result
    : typeof parsed?.output === 'string' ? parsed.output : null;
  if (text === null) return parsed;
  return JSON.parse(text.trim().replace(/^```json\s*/i, '').replace(/\s*```$/, ''));
}

function askAgy(prompt) {
  // 允许以 Node 脚本形式提供 Agy 替身（例如离线测试）。
  const script = /\.[cm]?js$/i.test(agy) ? [resolve(root, agy)] : [];
  const output = run(script.length ? process.execPath : agy,
    [...script, '--mode', 'plan', '--output-format', 'text', `--print=${prompt}`], { timeout: 600_000 });
  return parseAgyResponse(output);
}

const stringArray = value => Array.isArray(value) && value.every(item => typeof item === 'string' && item.trim());

function validateDiscussion(value) {
  if (!value || typeof value !== 'object') throw new Error('Agy discussion must be a JSON object');
  const operation = value.operation ?? 'add';
  if (typeof value.approved !== 'boolean' || !['add', 'supersede', 'retire'].includes(operation) ||
      !stringArray(value.evidence) || value.evidence.length === 0) {
    throw new Error('Agy discussion omitted approved, operation or evidence');
  }
  if (option('--review')) {
    const review = JSON.parse(readFileSync(resolve(root, option('--review')), 'utf8'));
    const sources = new Set(review.signals.flatMap(signal => signal.sources));
    if (value.evidence.some(source => !sources.has(source))) throw new Error('Agy discussion evidence is outside the current Session review');
  }
  if (operation === 'retire' ? !stringArray(value.retire) || !value.retire.length
    : !['high', 'normal', 'low'].includes(value.priority) || typeof value.instruction !== 'string' || !value.instruction.trim() ||
      (operation === 'supersede' && (!stringArray(value.supersedes) || !value.supersedes.length))) {
    throw new Error(`Agy discussion omitted the fields required by operation ${operation}`);
  }
  if (value.signals !== undefined && !stringArray(value.signals)) throw new Error('Agy discussion signals must be strings');
  return { ...value, operation };
}

function validateConfirmation(value) {
  if (!value || typeof value !== 'object' || typeof value.approved !== 'boolean' || typeof value.reason !== 'string') {
    throw new Error('Agy confirmation must include approved and reason');
  }
  return value;
}

function reviewHistory(roundDir) {
  const reviewFile = option('--review');
  if (reviewFile) {
    const review = JSON.parse(readFileSync(resolve(root, reviewFile), 'utf8'));
    if (!Array.isArray(review.runs) || !Array.isArray(review.signals)) throw new Error('Invalid serialized Session review');
    const sources = new Set(review.runs.flatMap(run => [run.source, run.skillReads?.source, run.knowledgeReads?.source, run.retryOf ? run.source + ':retry' : undefined]).filter(Boolean));
    if (review.signals.some(signal => !Array.isArray(signal.sources) || !signal.sources.length || signal.sources.some(source => !sources.has(source)))) {
      throw new Error('Session review contains unverifiable sources');
    }
    writeFileSync(join(roundDir, 'history.json'), JSON.stringify(review, null, 2) + '\n');
    return { source: reviewFile, history: review };
  }
  const session = option('--session');
  const execution = option('--execution');
  const historyFile = option('--history');
  let source;
  let history;
  if (historyFile) {
    source = historyFile;
    history = JSON.parse(readFileSync(resolve(root, historyFile), 'utf8'));
  } else if (session) {
    source = `session:${session}`;
    history = evolve('session', 'review', session);
  } else if (execution) {
    source = `execution:${execution}`;
    history = evolve('skill', 'review', execution, '--project', required('--project'));
  } else {
    source = 'none';
    history = { signals: [], note: 'No committed Session/Skill receipt was supplied for this round.' };
  }
  // 已被先前回合或银行记录处理过的信号不再驱动新提案。
  const signals = Array.isArray(history?.signals) ? history.signals : [];
  const open = signals.filter(signal => !addressedSignals.has(signal.id));
  const reviewed = { ...history, signals: open, addressed_signals: signals.length - open.length };
  writeFileSync(join(roundDir, 'history.json'), JSON.stringify(reviewed, null, 2) + '\n');
  return { source, history: reviewed };
}

function promptForDiscussion(roundDir, baseline, history, previous, feedback, learnings) {
  const instruction = [
    'You are the Agy discussion gate for workflow-evolve.',
    `Read the Skill entry at ${entry} and the round files in ${roundDir}.`,
    'Review the real Codex task result, memory search trace, and supplied execution history.',
    'Propose one minimal Skill change that fixes the attributed failure while preserving every passing scenario.',
    'Choose operation: add (new learning), supersede (replace listed learning IDs that are wrong or ineffective with one new instruction), or retire (remove harmful or obsolete learning IDs without new text).',
    learnings.consolidate_recommended
      ? `The Skill already holds too many learnings (${learnings.reasons.join('; ')}): do not use add; supersede or retire existing learnings, or set approved false so the candidate routes to ew-skill-design.`
      : 'Prefer supersede over add when an existing learning covers the same trigger.',
    'Return strict JSON with fields approved, operation, priority, instruction, when, supersedes, retire, signals, evidence, rationale, needs_revision.',
    'approved is true only when the suggestion is evidence-backed and bounded. priority is high, normal, or low (not needed for retire).',
    'instruction must be one concise line. evidence and signals (addressed signal IDs) must be arrays of strings. Do not edit files.',
    `Skill learnings: ${JSON.stringify(learnings.learnings ?? [])}`,
    `Previous Agy response: ${previous ? JSON.stringify(previous) : 'none'}`,
    `Previous attempt feedback (fix these before proposing again): ${feedback.length ? JSON.stringify(feedback) : 'none'}`,
    `Baseline observations: ${JSON.stringify(baseline)}`,
    `History: ${JSON.stringify(history)}`,
  ].join('\n');
  return askAgy(instruction);
}

function promptForConfirmation(roundDir, discussion, replay) {
  return askAgy([
    'You are the final Agy confirmation gate for workflow-evolve.',
    `Read the Skill entry at ${entry} and the round files in ${roundDir}.`,
    'Confirm whether the proposed Skill change is safe to apply after the baseline/candidate replay.',
    'Return strict JSON with fields approved, reason, required_changes. approved must be false if any baseline pass regresses, the evidence is missing, or the replay is incomplete.',
    `Discussion: ${JSON.stringify(discussion)}`,
    `Replay receipt: ${JSON.stringify(replay)}`,
  ].join('\n'));
}

function createDraft(discussion) {
  const evidence = discussion.evidence.flatMap(item => ['--evidence', item]);
  if (discussion.operation === 'retire') {
    return evolve('skill', 'retire', entry, ...discussion.retire, ...evidence,
      ...(discussion.rationale ? ['--reason', String(discussion.rationale).replace(/[\r\n]+/g, ' ')] : []));
  }
  return evolve('skill', 'draft', entry, discussion.instruction, '--priority', discussion.priority,
    ...(discussion.when ? ['--when', discussion.when] : []), ...evidence,
    ...(discussion.operation === 'supersede' ? discussion.supersedes.flatMap(id => ['--supersedes', id]) : []));
}

function addressedBy(discussion, signals) {
  const cited = new Set([...(discussion.signals ?? []), ...discussion.evidence]);
  return signals.filter(signal => cited.has(signal.id) || (signal.sources ?? []).some(source => cited.has(source))).map(signal => signal.id);
}

function saveBank(passedScenarios, addressed, receipt) {
  const at = new Date().toISOString();
  for (const id of addressed) addressedSignals.add(id);
  bank.scenarios = passedScenarios.map(scenario => ({ ...scenario, baseline: true }));
  bank.addressed_signals = [
    ...bank.addressed_signals,
    ...addressed.filter(id => !bank.addressed_signals.some(item => item.id === id)).map(id => ({ id, receipt, at })),
  ];
  bank.updated_at = at;
  mkdirSync(dirname(bankPath), { recursive: true });
  writeFileSync(bankPath, JSON.stringify(bank, null, 2) + '\n');
}

try {
  for (let round = 1; round <= iterations; round += 1) {
    const dirtyPackage = git(['status', '--porcelain', '--untracked-files=all', '--', packageDir]);
    if (dirtyPackage) {
      throw new Error(`Target Skill package has uncommitted changes (${packageDir}): commit or clean before running workflow-evolve`);
    }
    const roundDir = join(outputDir, `round-${round}`);
    mkdirSync(roundDir, { recursive: true });
    const base = git(['rev-parse', 'HEAD']);
    const history = reviewHistory(roundDir);
    if (option('--review') && !history.history.signals.length) {
      state.rounds.push({ round, base, status: 'no_signals', history: history.source });
      break;
    }
    const baseline = scenarioSet.map(scenario => ({ id: scenario.id, expected: scenario.input?.expected,
      observation: runReplay(scenario, base) }));
    writeFileSync(join(roundDir, 'baseline.json'), JSON.stringify(baseline, null, 2) + '\n');
    const regressions = baseline.filter(item => scenarioSet.find(scenario => scenario.id === item.id)?.baseline && !item.observation.passed);
    if (regressions.length) {
      state.rounds.push({ round, base, status: 'regression_detected', regressions });
      break;
    }
    const unresolved = baseline.filter(item => {
      const scenario = scenarioSet.find(candidate => candidate.id === item.id);
      return scenario?.baseline === false && !item.observation.passed;
    });
    scenarioSet = scenarioSet.map(scenario => {
      const result = baseline.find(item => item.id === scenario.id);
      return result?.observation.passed ? { ...scenario, baseline: true } : scenario;
    });
    if (!unresolved.length) {
      state.rounds.push({ round, base, status: 'converged', history: 'all scenarios passed' });
      saveBank(scenarioSet, [], null);
      break;
    }
    if (!allowEmptyHistory && !(history.history.signals.length > 0)) {
      state.rounds.push({ round, base, status: 'no_attributable_signals', history: history.source,
        addressed_signals: history.history.addressed_signals });
      break;
    }
    const learnings = evolve('skill', 'learnings', entry);
    writeFileSync(join(roundDir, 'learnings.json'), JSON.stringify(learnings, null, 2) + '\n');
    const scenarioPath = join(roundDir, 'scenarios.json');
    writeFileSync(scenarioPath, JSON.stringify(scenarioSet, null, 2) + '\n');
    // 讨论 → 草稿 → 回放失败时把具体原因回灌给下一次提案，直到耗尽重试次数。
    const feedback = [];
    let discussion;
    let draft;
    let replay;
    let blockedByBloat = false;
    for (let attempt = 0; attempt <= maxRetries && !replay; attempt += 1) {
      discussion = validateDiscussion(promptForDiscussion(roundDir, baseline, history.history, discussion, feedback, learnings));
      writeFileSync(join(roundDir, `agy-discussion-${attempt + 1}.json`), JSON.stringify(discussion, null, 2) + '\n');
      writeFileSync(join(roundDir, 'agy-discussion.json'), JSON.stringify(discussion, null, 2) + '\n');
      if (!discussion.approved) {
        feedback.push({ attempt: attempt + 1, stage: 'discussion', reason: discussion.rationale ?? discussion.needs_revision ?? 'not approved' });
        continue;
      }
      blockedByBloat = learnings.consolidate_recommended && discussion.operation === 'add';
      if (blockedByBloat) {
        feedback.push({ attempt: attempt + 1, stage: 'gate', reason: `learning bloat (${learnings.reasons.join('; ')}): add is not allowed` });
        continue;
      }
      try {
        draft = createDraft(discussion);
        writeFileSync(join(roundDir, 'draft.json'), JSON.stringify(draft, null, 2) + '\n');
        replay = evolve('skill', 'replay', join(roundDir, 'draft.json'), '--runner', runner, '--scenarios', scenarioPath);
      } catch (error) {
        const reason = failureText(error);
        feedback.push({ attempt: attempt + 1, stage: draft ? 'replay' : 'draft', reason });
        writeFileSync(join(roundDir, `replay-error-${attempt + 1}.json`), JSON.stringify({ draft: draft?.id, reason }, null, 2) + '\n');
        draft = undefined;
      }
    }
    if (!replay) {
      const status = !discussion?.approved ? 'agy_rejected' : blockedByBloat ? 'consolidation_required' : 'replay_failed';
      state.rounds.push({ round, base, status, retries: feedback.length, feedback, history: history.source });
      break;
    }
    writeFileSync(join(roundDir, 'replay.json'), JSON.stringify(replay, null, 2) + '\n');
    const confirmation = validateConfirmation(promptForConfirmation(roundDir, discussion, replay));
    writeFileSync(join(roundDir, 'agy-confirmation.json'), JSON.stringify(confirmation, null, 2) + '\n');
    const result = { round, base, operation: discussion.operation, draft: draft.id, replay: replay.id, history: history.source,
      retries: feedback.length, agy: confirmation };
    if (!confirmation.approved) {
      state.rounds.push({ ...result, status: 'confirmation_rejected' });
      break;
    }
    if (has('--auto')) {
      const applied = evolve('skill', 'apply', replay.id);
      let verified;
      try {
        verified = scenarioSet.map(scenario => ({ id: scenario.id, observation: runReplay(scenario, applied.commit) }));
      } catch (error) {
        const rollback = evolve('skill', 'rollback', replay.id);
        state.rounds.push({ ...result, status: 'rolled_back', applied, rollback, reason: 'Post-apply runner failed' });
        throw error;
      }
      const passed = verified.every(item => item.observation.passed);
      if (!passed) {
        evolve('skill', 'rollback', replay.id);
        state.rounds.push({ ...result, status: 'rolled_back', applied, verified });
        break;
      }
      const addressed = addressedBy(discussion, history.history.signals);
      saveBank(scenarioSet, addressed, replay.id);
      state.rounds.push({ ...result, status: 'applied', applied, verified, addressed_signals: addressed });
    } else {
      state.rounds.push({ ...result, status: 'confirmed_pending_apply' });
      break;
    }
    writeFileSync(statePath, JSON.stringify(state, null, 2) + '\n');
  }
  const lastRound = state.rounds.at(-1);
  const finalStatus = lastRound?.status;
  if (finalStatus === 'applied' || finalStatus === 'converged') {
    state.status = 'completed';
  } else if (finalStatus === 'confirmed_pending_apply') {
    state.status = 'confirmed_pending_apply';
  } else {
    state.status = finalStatus ?? 'failed';
  }
  state.finished_at = new Date().toISOString();
  state.state_sha256 = createHash('sha256').update(JSON.stringify(state)).digest('hex');
  writeFileSync(statePath, JSON.stringify(state, null, 2) + '\n');
  const report = { output_dir: outputDir, ...state };
  const runDir = option('--run-dir', process.env.RUN_DIR);
  const reportPath = runDir ? join(runDir, 'outputs', 'evolution-report.json') : join(outputDir, 'evolution-report.json');
  mkdirSync(dirname(reportPath), { recursive: true });
  const artifact = runDir ? { ...report, _meta: {
    kind: 'workflow-evolution-report', schema: 'workflow-evolution/1.0', role: 'primary', alias: 'latest-workflow-evolution',
  } } : report;
  writeFileSync(reportPath, JSON.stringify(artifact, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
} catch (error) {
  state.status = 'failed';
  state.error = error instanceof Error ? error.message : String(error);
  writeFileSync(statePath, JSON.stringify(state, null, 2) + '\n');
  throw error;
}
