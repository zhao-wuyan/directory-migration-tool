"""ACO controller CLI - the script side of ew-swarm.

Subcommands: init | select | update | converged | report
Spec: ../specs/swarm-protocol.md (script <-> coordinator contract)

All commands:
  - read state from --session <path>
  - emit JSON to stdout
  - exit 0 success, 1 runtime error, 2 config invalid

Invoked by ew-swarm coordinator via Bash. No prose output, no interactive prompts.
"""
from __future__ import annotations

import argparse
import glob
import hashlib
import json
import os
import random
import re
import sys
import tempfile
import time
from contextlib import contextmanager
from pathlib import Path
from typing import List, Optional

# Local imports (script directory)
sys.path.insert(0, str(Path(__file__).parent))
from pheromone import PheromoneState  # noqa: E402
from scoring import (  # noqa: E402
    FallbackScorer,
    ScriptScorer,
    hallucination_check,
    load_verified_scores,
    resolve_score,
)

VERSION = "1.0"


# ---------------------------------------------------------------------------
# Session paths
# ---------------------------------------------------------------------------

class SessionPaths:
    def __init__(self, session: Path, run_dir: Optional[Path] = None):
        self.session = session
        self.config = session / "swarm-config.json"
        self.pheromone_dir = session / "pheromone"
        self.pheromone_current = self.pheromone_dir / "current.json"
        self.pheromone_init = self.pheromone_dir / "init.json"
        self.pheromone_history = self.pheromone_dir / "history"
        self.task_space = session / "task-space.json"
        self.trails = session / "trails"
        self.artifacts = session / "artifacts"
        self.scores = session / "scores"
        self.assignments = session / "assignments"
        self.best = session / "best.json"
        self.updates = self.pheromone_dir / "updates"
        # run session: formal deliverables live in {run_dir}/outputs/
        self.run_dir = run_dir
        self.outputs = (run_dir / "outputs") if run_dir else self.artifacts

    def ensure_dirs(self) -> None:
        for d in [self.pheromone_dir, self.pheromone_history, self.trails, self.artifacts, self.scores]:
            d.mkdir(parents=True, exist_ok=True)


def _emit(payload: dict) -> None:
    print(json.dumps(payload, ensure_ascii=False))


def _fail(code: int, msg: str):
    _emit({"status": "error", "message": msg})
    sys.exit(code)


def _json(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False, sort_keys=True)


def _write_atomic(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as file:
            file.write(content)
            file.flush()
            os.fsync(file.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


@contextmanager
def _controller_lock(session: Path):
    with (session / ".aco.lock").open("a+b") as lock:
        if os.name == "nt":
            from msvcrt import locking, LK_NBLCK
            lock.seek(0)
            locking(lock.fileno(), LK_NBLCK, 1)
        else:
            from fcntl import flock, LOCK_EX, LOCK_NB
            flock(lock.fileno(), LOCK_EX | LOCK_NB)
        yield


def _project_update(paths: SessionPaths, iteration: int, receipt: dict) -> None:
    if receipt["best"] is not None:
        _write_atomic(paths.best, _json(receipt["best"]))
    state = _json(receipt["state"])
    _write_atomic(paths.pheromone_history / f"{iteration}.json", state)
    trails = "\n".join(json.dumps(line, ensure_ascii=False, sort_keys=True) for line in receipt["trails"])
    _write_atomic(paths.trails / f"{iteration}.jsonl", trails)
    _write_atomic(paths.pheromone_current, state)

# ---------------------------------------------------------------------------
# init
# ---------------------------------------------------------------------------

def _discover_nodes(spec: dict) -> List[str]:
    """Resolve task_space.nodes — supports explicit list or auto_discover_from glob."""
    if isinstance(spec.get("nodes"), list):
        return [str(n) for n in spec["nodes"]]
    if "auto_discover_from" in spec:
        pattern = spec["auto_discover_from"]
        # Glob relative to cwd (workspace root), not session
        matches = sorted(glob.glob(pattern, recursive=True))
        if not matches:
            raise ValueError(f"auto_discover_from '{pattern}' matched no files")
        return matches
    raise ValueError("task_space requires either 'nodes' list or 'auto_discover_from' glob")


def _run_dir(args: argparse.Namespace) -> Optional[Path]:
    rd = getattr(args, "run_dir", None)
    return Path(rd) if rd else None


def cmd_init(args: argparse.Namespace) -> None:
    paths = SessionPaths(Path(args.session), _run_dir(args))
    if not paths.config.exists():
        _fail(2, f"config not found: {paths.config}")
    if paths.pheromone_current.is_file() and PheromoneState.load(paths.pheromone_current).iteration > 0:
        _fail(1, "cannot reinitialize an active Swarm")
    if paths.run_dir and any(paths.assignments.glob("*.json")):
        _fail(1, "cannot reinitialize a Swarm with recorded assignments")
    config = json.loads(paths.config.read_text(encoding="utf-8"))

    paths.ensure_dirs()

    try:
        nodes = _discover_nodes(config.get("task_space", {}))
    except ValueError as e:
        _fail(2, str(e))
        return  # unreachable, satisfies type checker

    # Write task-space.json
    task_space = {
        "nodes": nodes,
        "n_nodes": len(nodes),
        "max_path_length": config.get("task_space", {}).get("max_path_length", 5),
        "start_nodes": config.get("task_space", {}).get("start_nodes", "any"),
        "edges": config.get("task_space", {}).get("edges", "complete"),
    }
    paths.task_space.write_text(json.dumps(task_space, indent=2, ensure_ascii=False), encoding="utf-8")

    # Initialize pheromone
    aco_cfg = config.get("aco", {})
    state = PheromoneState.initialize(nodes, aco_cfg)
    state.save(paths.pheromone_current)
    state.save(paths.pheromone_init)

    _emit({
        "status": "ok",
        "command": "init",
        "session": str(paths.session),
        "pheromone_path": str(paths.pheromone_current),
        "task_space_path": str(paths.task_space),
        "n_nodes": len(nodes),
        "n_edges": len(state.tau),
    })


# ---------------------------------------------------------------------------
# select
# ---------------------------------------------------------------------------

def _pick_start_node(nodes: List[str], state: PheromoneState, mode: str) -> str:
    if mode == "weighted":
        weights = [state.node_tau.get(n, 1.0) for n in nodes]
        total = sum(weights)
        if total == 0:
            return random.choice(nodes)
        r = random.uniform(0, total)
        acc = 0.0
        for n, w in zip(nodes, weights):
            acc += w
            if acc >= r:
                return n
        return nodes[-1]
    return random.choice(nodes)


def cmd_select(args: argparse.Namespace) -> None:
    paths = SessionPaths(Path(args.session), _run_dir(args))
    selection = paths.assignments / f"{args.iter}.json"
    if paths.run_dir and selection.is_file():
        _emit(json.loads(selection.read_text(encoding="utf-8")))
        return
    state = PheromoneState.load(paths.pheromone_current)
    if paths.run_dir and args.iter != state.iteration + 1:
        _fail(1, f"expected iteration {state.iteration + 1} for a new assignment batch")
    config = json.loads(paths.config.read_text(encoding="utf-8"))
    task_space = json.loads(paths.task_space.read_text(encoding="utf-8"))

    n_ants = config.get("swarm", {}).get("n_ants", 5)
    nodes = task_space["nodes"]
    max_len = task_space.get("max_path_length", 5)
    start_mode = task_space.get("start_nodes", "any")

    assignments = []
    for i in range(1, n_ants + 1):
        start = _pick_start_node(nodes, state, "weighted" if start_mode == "weighted" else "uniform")
        edge_prefs = state.select_neighbors(start, nodes)
        # Keep top-k edges to reduce noise
        top_k = sorted(edge_prefs.items(), key=lambda x: -x[1])[:8]
        assignments.append({
            "ant_id": f"ANT-{args.iter}-{i}",
            "start_node": start,
            "edge_preferences": {f"{start}::{b}": w for b, w in top_k},
            "max_path_length": max_len,
            "iteration": args.iter,
        })

    result = {
        "status": "ok",
        "command": "select",
        "iteration": args.iter,
        "n_assignments": len(assignments),
        "assignments": assignments,
    }
    if paths.run_dir:
        _write_atomic(selection, _json(result))
    _emit(result)


# ---------------------------------------------------------------------------
# update
# ---------------------------------------------------------------------------

def _load_iteration_artifacts(paths: SessionPaths, iteration: int) -> List[dict]:
    pattern = str(paths.outputs / f"ant-{iteration}-*.json")
    files = sorted(glob.glob(pattern))
    if not files and paths.run_dir and paths.artifacts != paths.outputs:
        pattern = str(paths.artifacts / f"ant-{iteration}-*.json")
        files = sorted(glob.glob(pattern))
    artifacts = []
    for f in files:
        try:
            artifact = json.loads(Path(f).read_text(encoding="utf-8"))
        except json.JSONDecodeError as e:
            if paths.run_dir:
                _fail(1, f"malformed ant artifact: {Path(f).name}")
            print(f"warning: skipped malformed artifact {f}: {e}", file=sys.stderr)
            continue
        if paths.run_dir:
            expected_id = Path(f).stem.upper()
            if not isinstance(artifact, dict) or artifact.get("ant_id") != expected_id:
                _fail(1, f"ant artifact ID must match {Path(f).name}")
        artifacts.append(artifact)
    return artifacts


def _validate_artifact(art: dict, valid_nodes: set, iteration: int, require_meta: bool = False) -> Optional[str]:
    if not isinstance(art, dict):
        return "artifact must be an object"
    if require_meta:
        meta = art.get("_meta")
        if not isinstance(meta, dict):
            return "missing or invalid _meta"
        if (meta.get("kind"), meta.get("schema"), meta.get("role")) != (
            "swarm-ant-result", "swarm-ant-result/1.0", "evidence"
        ):
            return "invalid ant artifact contract"
    required = ["schema_version", "ant_id", "iteration", "path", "self_score", "self_confidence"]
    for f in required:
        if f not in art:
            return f"missing field: {f}"
    if not isinstance(art["path"], list) or not art["path"]:
        return "path must be non-empty list"
    if (art["schema_version"] != "1.0" or type(art["iteration"]) is not int or art["iteration"] != iteration
        or not re.fullmatch(rf"ANT-{iteration}-[1-9][0-9]*", str(art["ant_id"]))):
        return "invalid ant identity, schema or iteration"
    for n in art["path"]:
        if not isinstance(n, str) or n not in valid_nodes:
            return f"path node '{n}' not in task_space"
    for field in ("self_score", "self_confidence"):
        if type(art[field]) not in (int, float) or not 0.0 <= art[field] <= 1.0:
            return f"{field} out of [0,1]"
    if require_meta:
        evidence, solution = art.get("evidence"), art.get("candidate_solution")
        if not isinstance(evidence, list) or not evidence or any(not isinstance(item, str) or not item.strip() for item in evidence):
            return "missing ant evidence"
        if not isinstance(solution, dict) or not isinstance(solution.get("summary"), str) or not solution["summary"].strip():
            return "missing candidate solution"
        decisions, path = art.get("path_decisions"), art["path"]
        if not isinstance(decisions, list) or len(decisions) != len(path) - 1:
            return "path decisions do not match path"
        if any(not isinstance(item, dict) or (item.get("from"), item.get("to")) != (start, end)
               for item, start, end in zip(decisions, path, path[1:])):
            return "path decision endpoints do not match path"
    return None


def cmd_update(args: argparse.Namespace) -> None:
    paths = SessionPaths(Path(args.session), _run_dir(args))
    config = json.loads(paths.config.read_text(encoding="utf-8"))
    state = PheromoneState.load(paths.pheromone_current)
    task_space = json.loads(paths.task_space.read_text(encoding="utf-8"))
    valid_nodes = set(task_space["nodes"])

    artifacts = _load_iteration_artifacts(paths, args.iter)
    if not artifacts:
        _fail(1, f"no artifacts found for iteration {args.iter}")

    # Resolve scorers
    scoring_cfg = config.get("scoring", {})
    llm_run = paths.run_dir is not None and scoring_cfg.get("mode") == "llm"
    if args.allow_score_fallback and not llm_run:
        _fail(2, "--allow-score-fallback applies to a Run using llm scoring")
    rule = None
    if scoring_cfg.get("mode") in ("script", "hybrid"):
        rule_path = scoring_cfg.get("script_path")
        if rule_path:
            # Relative scoring scripts belong to the project cwd, not the Run's work/team parent.
            full = Path(rule_path) if Path(rule_path).is_absolute() else Path.cwd() / rule_path
            if not full.is_file():
                _fail(2, f"scoring script not found: {full}")
            rule = full
    fallback = FallbackScorer(scoring_cfg.get("self_score_discount", 0.5))
    score_path = paths.scores / f"iter-{args.iter}-scores.json"
    verified_scores = {} if args.allow_score_fallback else load_verified_scores(
        score_path, iteration=args.iter if llm_run else None)
    selected_path = paths.assignments / f"{args.iter}.json"
    selection = None
    if paths.run_dir and selected_path.is_file():
        selection = json.loads(selected_path.read_text(encoding="utf-8"))
    recipe = {
        "config": config, "task_space": task_space, "artifacts": artifacts,
        "verified_scores": verified_scores,
        "script_hash": hashlib.sha256(rule.read_bytes()).hexdigest() if rule else None,
    }
    if args.allow_score_fallback:
        recipe["allow_score_fallback"] = True
    if selection is not None:
        recipe["assignment_batch"] = selection
    digest = hashlib.sha256(_json(recipe).encode("utf-8")).hexdigest()
    receipt_path = paths.updates / f"{args.iter}.json"
    if receipt_path.is_file():
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        if receipt["input_sha256"] != digest:
            _fail(1, f"iteration {args.iter} inputs changed after update")
        _emit(receipt["result"])
        return
    if args.iter != state.iteration + 1:
        _fail(1, f"expected iteration {state.iteration + 1}; completed legacy iterations lack replay receipts")
    if llm_run and not args.allow_score_fallback and not score_path.is_file():
        _fail(1, f"missing LLM scores file for iteration {args.iter}: {score_path.name}")
    script_scorer = ScriptScorer(rule) if rule else None

    selected_starts = None
    if selection is not None:
        selected_starts = {item["ant_id"]: item["start_node"] for item in selection["assignments"]}
    valid_artifacts = {}
    for art in artifacts:
        err = _validate_artifact(art, valid_nodes, args.iter, require_meta=paths.run_dir is not None)
        if err:
            if paths.run_dir is not None:
                _fail(1, f"invalid ant artifact: {err}")
            print(f"warning: invalid ant artifact: {err}", file=sys.stderr)
            continue
        if art["ant_id"] in valid_artifacts:
            _fail(1, f"duplicate ant identity: {art['ant_id']}")
        if selected_starts is not None and selected_starts.get(art["ant_id"]) != art["path"][0]:
            _fail(1, f"ant artifact does not match the saved assignment: {art['ant_id']}")
        valid_artifacts[art["ant_id"]] = art

    if llm_run and not args.allow_score_fallback:
        missing = sorted(valid_artifacts.keys() - verified_scores.keys())
        unexpected = sorted(verified_scores.keys() - valid_artifacts.keys())
        if missing or unexpected:
            _fail(1, f"LLM scores do not match iteration {args.iter} artifacts: missing {missing}, unexpected {unexpected}")

    state.evaporate()
    trail_log = []
    hallucinations = []
    scored = []
    for art in valid_artifacts.values():
        score, src = resolve_score(art, verified_scores, script_scorer, fallback)
        if type(score) not in (int, float) or not 0.0 <= score <= 1.0:
            _fail(1, f"invalid score for {art['ant_id']}")

        if src == "verified_llm":
            if hallucination_check(art["self_score"], score):
                hallucinations.append(art["ant_id"])
                score *= 0.5
        scored.append({"ant_id": art["ant_id"], "score": score, "source": src})

        state.deposit(art["path"], score)
        trail_log.append({
            "ant_id": art["ant_id"],
            "path": art["path"],
            "self_score": art["self_score"],
            "verified_score": score,
            "source": src,
        })

    if not scored:
        _fail(1, f"no valid artifacts for iteration {args.iter}")
    # Elitist: re-load best history, deposit extra on best path
    best_data = None
    if paths.best.exists():
        best_data = json.loads(paths.best.read_text(encoding="utf-8"))
    prev_best = best_data["score"] if best_data else 0.0
    current_best = max(scored, key=lambda x: x["score"]) if scored else None
    if current_best:
        best_art = valid_artifacts[current_best["ant_id"]]
        if best_data is None or current_best["score"] > best_data.get("score", -1):
            best_data = {
                "ant_id": current_best["ant_id"],
                "iteration": args.iter,
                "path": best_art["path"],
                "score": current_best["score"],
                "self_score": best_art["self_score"],
                "candidate_solution": best_art.get("candidate_solution"),
                "evidence": best_art.get("evidence", []),
                "updated_at": time.time(),
            }
        # Elite deposit
        state.deposit(best_data["path"], best_data["score"])

    state.clip()
    state.iteration = args.iter
    mean_score = sum(s["score"] for s in scored) / len(scored) if scored else 0.0
    best_score = best_data["score"] if best_data else 0.0
    delta = best_score - prev_best

    result = {
        "status": "ok",
        "command": "update",
        "iteration": args.iter,
        "n_ants_processed": len(scored),
        "mean_score": round(mean_score, 4),
        "best_score": round(best_score, 4),
        "delta": round(delta, 4),
        "elite_updated": current_best is not None and (best_data is None or current_best["ant_id"] == best_data["ant_id"]),
        "hallucinations_flagged": hallucinations,
        "stats": state.stats(),
    }
    receipt = {
        "input_sha256": digest, "state": state.to_dict(), "best": best_data,
        "trails": trail_log, "result": result,
    }
    if llm_run:
        receipt["scoring"] = "fallback" if args.allow_score_fallback else "llm"
    _write_atomic(receipt_path, _json(receipt))
    _project_update(paths, args.iter, receipt)
    _emit(result)


# ---------------------------------------------------------------------------
# converged
# ---------------------------------------------------------------------------

def cmd_converged(args: argparse.Namespace) -> None:
    paths = SessionPaths(Path(args.session), _run_dir(args))
    config = json.loads(paths.config.read_text(encoding="utf-8"))
    cv = config.get("convergence", {})

    state = PheromoneState.load(paths.pheromone_current)
    iteration = state.iteration

    triggered = []
    metrics = {
        "iteration": iteration,
        "entropy": state.stats()["entropy"],
        "best_score": 0.0,
        "mean_score": 0.0,
        "iterations_since_best_change": 0,
    }

    if paths.best.exists():
        metrics["best_score"] = json.loads(paths.best.read_text(encoding="utf-8")).get("score", 0.0)

    # max_iterations
    max_iter = cv.get("max_iterations", 5)
    if iteration >= max_iter:
        triggered.append("max_iterations")

    # entropy_floor
    ef = cv.get("entropy_floor", {})
    if ef.get("enabled", True) and metrics["entropy"] < ef.get("threshold", 0.5):
        triggered.append("entropy_floor")

    # target_score
    ts = cv.get("target_score", {})
    if ts.get("enabled", True) and metrics["best_score"] >= ts.get("value", 0.95):
        triggered.append("target_score")

    # stagnation
    st = cv.get("stagnation", {})
    if st.get("enabled", True):
        patience = st.get("patience", 2)
        min_delta = st.get("min_delta", 0.01)
        # Use trails to compute per-iter best
        trail_files = sorted(paths.trails.glob("*.jsonl"))
        per_iter_best = []
        for tf in trail_files:
            lines = [json.loads(l) for l in tf.read_text().splitlines() if l.strip()]
            if lines:
                per_iter_best.append(max(l.get("verified_score", 0) for l in lines))
        if len(per_iter_best) > patience:
            recent = per_iter_best[-patience - 1:]
            deltas = [abs(recent[i] - recent[i - 1]) for i in range(1, len(recent))]
            metrics["iterations_since_best_change"] = sum(1 for d in deltas if d < min_delta)
            if all(d < min_delta for d in deltas):
                triggered.append("stagnation")

    _emit({
        "status": "ok",
        "command": "converged",
        "converged": len(triggered) > 0,
        "iteration": iteration,
        "triggered_by": triggered,
        "reason": triggered[0] if triggered else "in_progress",
        "metrics": metrics,
        "recommendation": "ready for report" if triggered else f"continue to iteration {iteration + 1}",
    })


# ---------------------------------------------------------------------------
# report
# ---------------------------------------------------------------------------

def cmd_report(args: argparse.Namespace) -> None:
    paths = SessionPaths(Path(args.session), _run_dir(args))
    state = PheromoneState.load(paths.pheromone_current)

    best = None
    if paths.best.exists():
        best = json.loads(paths.best.read_text(encoding="utf-8"))

    # Top-K trails across all iterations
    all_trails = []
    for tf in sorted(paths.trails.glob("*.jsonl")):
        for line in tf.read_text().splitlines():
            if line.strip():
                all_trails.append(json.loads(line))
    top_k = sorted(all_trails, key=lambda x: -x.get("verified_score", 0))[:5]

    # Convergence curve
    curve = []
    for hf in sorted(paths.pheromone_history.glob("*.json"), key=lambda p: int(p.stem)):
        snap = json.loads(hf.read_text(encoding="utf-8"))
        curve.append({
            "iteration": snap["iteration"],
            "entropy": snap["stats"]["entropy"],
            "tau_max": snap["stats"]["max"],
            "tau_mean": snap["stats"]["mean"],
        })

    _emit({
        "_meta": {
            "kind": "swarm-report",
            "schema": "swarm-report/1.0",
            "role": "attachment",
        },
        "status": "ok",
        "command": "report",
        "best": best,
        "top_k": top_k,
        "convergence_curve": curve,
        "final_pheromone_stats": state.stats(),
        "iterations_completed": state.iteration,
    })


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="aco", description=f"ACO controller v{VERSION}")
    p.add_argument("--session", required=True, help="path to session folder")
    p.add_argument("--run-dir", dest="run_dir", default=None, help="run session outputs directory (formal deliverables)")
    sub = p.add_subparsers(dest="command", required=True)

    sub.add_parser("init", help="initialize pheromone + task-space from config")

    s_select = sub.add_parser("select", help="produce N ant assignments for iteration k")
    s_select.add_argument("--iter", type=int, required=True)

    s_update = sub.add_parser("update", help="update pheromone from iteration artifacts")
    s_update.add_argument("--iter", type=int, required=True)
    s_update.add_argument("--allow-score-fallback", action="store_true",
                          help="score this LLM Run iteration using the fallback scorer")

    sub.add_parser("converged", help="check convergence criteria")
    sub.add_parser("report", help="emit full result report")

    return p


def main(argv: Optional[List[str]] = None) -> None:
    args = build_parser().parse_args(argv)
    handlers = {
        "init": cmd_init,
        "select": cmd_select,
        "update": cmd_update,
        "converged": cmd_converged,
        "report": cmd_report,
    }
    try:
        session = Path(args.session)
        if not session.is_dir():
            _fail(2, f"session directory not found: {session}")
        with _controller_lock(session):
            paths = SessionPaths(session, _run_dir(args))
            if paths.pheromone_current.is_file():
                current = PheromoneState.load(paths.pheromone_current).iteration
                pending = paths.updates / f"{current + 1}.json"
                if pending.is_file():
                    receipt = json.loads(pending.read_text(encoding="utf-8"))
                    _project_update(paths, current + 1, receipt)
            handlers[args.command](args)
    except SystemExit:
        raise
    except Exception as e:
        _fail(1, f"{type(e).__name__}: {e}")


if __name__ == "__main__":
    main()
