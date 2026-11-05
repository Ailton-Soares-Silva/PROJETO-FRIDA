"""Homologacao real da busca no WhatsApp (le o WhatsApp autenticado do operador).

1. Gabarito: abre conversas reais e registra mensagens renderizadas (incluindo
   paginas antigas) com message_id, conversa, remetente e direcao.
2. Bateria: 30 consultas com mensagem-alvo conhecida (global, dentro da conversa,
   contatos, periodo), mais controles negativos (periodo excludente e ausencia).
3. Reinicios: tres novos processos do leitor retomando a mesma pesquisa pelo
   cursor, sem novo QR e sem repetir registros.

Saida: tests/results/whatsapp-homologation.json (contem mensagens reais; ignorado pelo git).
Uso:  python tests/homologate_whatsapp.py
"""
import json
import os
import random
import re
import statistics
import subprocess
import sys
import time
from collections import Counter
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from whatsapp_runtime import ReaderSession, ReaderFailure  # noqa: E402
from whatsapp_dom import WebUI, CHATS_JS, normalized, SAO_PAULO  # noqa: E402

RESULTS = ROOT / "tests" / "results"
OUTPUT = RESULTS / "whatsapp-homologation.json"
TARGETS = 30
WORD = re.compile(r"^[0-9A-Za-zÀ-ÖØ-öø-ÿ]+$")
STOP = set("a o e de da do das dos em no na nos nas um uma para por com que se ao aos as os mas mais ja eu voce ele ela "
           "isso esse essa este esta foi ser ter tem sim nao bom dia boa tarde noite ok obrigado obrigada pra pro".split())


def log(*args):
    print(*args, flush=True)


def phrase_for(text, frequency):
    """Two or three consecutive plain words, containing a rare word of 5+ letters."""
    words = text.split()
    best = None
    for size in (3, 2):
        for i in range(len(words) - size + 1):
            chunk = words[i:i + size]
            if not all(WORD.match(w) for w in chunk):
                continue
            keys = [normalized(w) for w in chunk]
            if all(k in STOP for k in keys) or not any(len(k) >= 5 and k not in STOP for k in keys):
                continue
            rarity = sum(frequency[k] for k in keys)
            if best is None or rarity < best[0]:
                best = (rarity, " ".join(chunk))
        if best:
            return best[1]
    return None


def collect_baseline(session, ui, max_chats=18):
    ui.reset_search()
    ui.wait(lambda: session.driver.execute_script(CHATS_JS, False), label="lista de conversas")
    names, seen = [], set()
    for _ in range(4):
        for row in session.driver.execute_script(CHATS_JS, False) or []:
            if row["name"] not in seen:
                seen.add(row["name"]); names.append(row["name"])
        ui.scroll("#pane-side", 1); time.sleep(.6)
    ui.reset_search()
    chats, messages = [], {}
    quota, kinds = {"group": 12, "contact": 8}, Counter()
    for name in names:
        if len(chats) >= max_chats or all(kinds[k] >= v for k, v in quota.items()):
            break
        try:
            identity = ui.open_chat(name)
        except ReaderFailure as exc:
            log("  gabarito: ignorada", repr(name), exc.status)
            continue
        if kinds[identity["chat_type"]] >= quota[identity["chat_type"]]:
            continue
        kinds[identity["chat_type"]] += 1
        page_rows = []
        for page in range(4):  # current screen + three older pages
            try:
                rendered = ui.messages(identity)
            except ReaderFailure:
                rendered = []
            for item in rendered:
                if item["message_id"] not in messages:
                    item["page"] = page
                    messages[item["message_id"]] = item
                    page_rows.append(item)
            ui.scroll("#main", -1); time.sleep(1.0)
        chats.append({"identity": identity, "messages": len(page_rows)})
        log("  gabarito:", identity["chat_type"], repr(identity["chat"]), len(page_rows), "mensagens")
    return chats, list(messages.values())


def choose_targets(messages):
    frequency = Counter(normalized(w) for m in messages for w in m["text"].split())
    phrases = Counter()
    candidates = []
    for m in messages:
        if not m.get("timestamp") or len(m["text"]) > 700 or re.search(r"https?://", m["text"]):
            continue
        phrase = phrase_for(m["text"], frequency)
        if phrase:
            candidates.append((m, phrase))
            phrases[normalized(phrase)] += 1
    # A phrase repeated in the baseline has no single known target.
    candidates = [(m, p) for m, p in candidates if phrases[normalized(p)] == 1
                  and sum(normalized(p) in normalized(x["text"]) for x in messages) == 1]
    random.Random(20261104).shuffle(candidates)
    contacts = [c for c in candidates if c[0]["chat_type"] == "contact"]
    groups = [c for c in candidates if c[0]["chat_type"] == "group"]
    old = [c for c in candidates if c[0]["page"] > 0]
    chosen, used = [], set()
    def take(pool, count):
        for m, p in pool:
            if len(chosen) >= TARGETS or count <= 0:
                return
            if m["message_id"] not in used:
                used.add(m["message_id"]); chosen.append((m, p)); count -= 1
    take(contacts, 8); take(old, 6); take(groups, TARGETS); take(candidates, TARGETS)
    plan = []
    for index, (m, phrase) in enumerate(chosen[:TARGETS]):
        mode = "global" if index % 30 < 18 else "within" if index % 30 < 26 else "within_period"
        plan.append({"index": index + 1, "mode": mode, "query": phrase, "expected": m})
    return plan


def day_bounds(timestamp):
    stamp = datetime.fromisoformat(timestamp).astimezone(SAO_PAULO)
    start = stamp.replace(hour=0, minute=0, second=0, microsecond=0)
    return start.isoformat(), (start + timedelta(days=1)).isoformat()


def run_query(session, request):
    events = []
    started = time.monotonic()
    session.emit = lambda event: events.append((round((time.monotonic() - started) * 1000), event))
    result = session.execute(request)
    batches = [ms for ms, e in events if e.get("event") == "batch"]
    progress = [ms for ms, e in events if e.get("event") == "progress"]
    return result, {"elapsed_ms": result.get("elapsed_ms"), "first_progress_ms": progress[0] if progress else None,
                    "first_batch_ms": batches[0] if batches else None}


def battery(session, plan, baseline):
    by_id = {m["message_id"]: m for m in baseline}
    rows = []
    for case in plan:
        m = case["expected"]
        request = {"operation": "search", "query": case["query"], "limit": 100}
        if case["mode"] != "global":
            request.update(chat_name=m["chat"], chat_type=m["chat_type"])
        if case["mode"] == "within_period":
            request["date_from"], request["date_to"] = day_bounds(m["timestamp"])
        result, timing = run_query(session, request)
        found = [r for r in result.get("results") or [] if r.get("message_id") == m["message_id"]]
        wrong = [r for r in result.get("results") or [] if r.get("message_id") in by_id
                 and normalized(r.get("chat")) != normalized(by_id[r["message_id"]]["chat"])]
        row = {"index": case["index"], "mode": case["mode"], "chat_type": m["chat_type"], "page": m["page"],
               "query": case["query"], "status": result.get("status"), "found": bool(found),
               "same_reference": bool(found) and found[0]["reference"] == m["reference"],
               "wrong_attribution": len(wrong), "returned": len(result.get("results") or []),
               "coverage": result.get("coverage"), "message": result.get("message"), **timing}
        rows.append(row)
        log("  %02d %-13s %-7s %-8s found=%-5s wrong=%d n=%-3s %6sms first=%sms  %r" % (
            case["index"], case["mode"], m["chat_type"], row["status"], row["found"], row["wrong_attribution"],
            row["returned"], row["elapsed_ms"], row["first_batch_ms"], case["query"]))
    return rows


def negatives(session, plan):
    rows = []
    for case in [c for c in plan if c["mode"] != "global"][:2]:
        m = case["expected"]
        start, _ = day_bounds(m["timestamp"])
        # A period ending at the start of the message's day must exclude it.
        early = (datetime.fromisoformat(start) - timedelta(days=400)).isoformat()
        result, timing = run_query(session, {"operation": "search", "query": case["query"], "chat_name": m["chat"],
                                             "chat_type": m["chat_type"], "date_from": early, "date_to": start})
        excluded = not any(r.get("message_id") == m["message_id"] for r in result.get("results") or [])
        rows.append({"kind": "period_excludes_target", "query": case["query"], "status": result.get("status"), "passed": excluded, **timing})
        log("  periodo excludente %r -> %s excluido=%s" % (case["query"], result.get("status"), excluded))
    for token in ("zzqxv" + str(random.Random(7).randint(10000, 99999)), "qqwzx semresultado"):
        result, timing = run_query(session, {"operation": "search", "query": token})
        # Correct outcomes: a confirmed absence, or (while WhatsApp syncs history) an
        # explicit refusal to confirm it. Never invented results.
        reason = (result.get("coverage") or {}).get("reason")
        ok = not result.get("results") and ((result.get("status") == "success" and reason == "confirmed_empty")
                                            or (result.get("status") == "partial" and reason == "history_sync_in_progress"))
        rows.append({"kind": "confirmed_absence", "query": token, "status": result.get("status"), "passed": ok, **timing})
        log("  ausencia %r -> %s %s" % (token, result.get("status"), (result.get("coverage") or {}).get("reason")))
    return rows


def direction_check(baseline):
    outgoing = Counter(m["sender"] for m in baseline if m["direction"] == "outgoing" and m["sender"])
    if not outgoing:
        return {"self_name": None, "checked": 0, "inconsistent": 0, "note": "nenhuma mensagem enviada no gabarito"}
    self_name = outgoing.most_common(1)[0][0]
    inconsistent = [m for m in baseline if m["sender"] and ((m["direction"] == "outgoing") != (m["sender"] == self_name))]
    return {"self_name_present": True, "checked": len(baseline), "outgoing": sum(outgoing.values()),
            "inconsistent": len(inconsistent), "examples": [{"chat": m["chat"], "sender": m["sender"], "direction": m["direction"]} for m in inconsistent[:5]]}


def cli(args, collect_seconds=None):
    env = dict(os.environ, PYTHONUTF8="1", PYTHONIOENCODING="utf-8")
    if collect_seconds:
        env["FRIDA_WHATSAPP_COLLECT_SECONDS"] = str(collect_seconds)
    started = time.monotonic()
    proc = subprocess.run([sys.executable, str(ROOT / "whatsapp_reader.py")] + args, capture_output=True, env=env, cwd=str(ROOT), timeout=300)
    out = proc.stdout.decode("utf-8", "replace").strip().splitlines()
    result = json.loads(out[-1]) if out else {"status": "no_output", "stderr": proc.stderr.decode("utf-8", "replace")[-800:]}
    result["_wall_ms"] = round((time.monotonic() - started) * 1000)
    return result


def restarts(term):
    """Each step is a new process (new browser) resuming the previous cursor."""
    steps, seen, cursor = [], set(), ""
    for step in range(4):
        args = ["--search-live", term] + (["--cursor", cursor] if cursor else [])
        result = cli(args, collect_seconds=5)
        refs = [r["reference"] for r in result.get("results") or []]
        repeated = len(set(refs) & seen)
        seen.update(refs)
        steps.append({"process": step + 1, "status": result.get("status"), "returned": len(refs), "repeated": repeated,
                      "pages": (result.get("coverage") or {}).get("pages"), "reason": (result.get("coverage") or {}).get("reason"),
                      "cursor_in": bool(cursor), "cursor_out": bool(result.get("cursor")), "wall_ms": result["_wall_ms"],
                      "timings_ms": result.get("timings_ms"), "message": result.get("message")})
        log("  processo %d: %s n=%d repetidos=%d cursor=%s %sms" % (step + 1, result.get("status"), len(refs), repeated, bool(result.get("cursor")), result["_wall_ms"]))
        if result.get("status") not in ("success", "partial"):
            break
        cursor = result.get("cursor") or ""
        if not cursor:
            break
    # Session persistence regardless of how soon the search finished.
    while len(steps) < 4:
        status = cli(["--status"])
        steps.append({"process": len(steps) + 1, "status": status.get("status"), "kind": "status_only", "wall_ms": status["_wall_ms"]})
        log("  processo %d (status): %s" % (len(steps), status.get("status")))
    return steps


def percentile(values, p):
    values = sorted(v for v in values if v is not None)
    if not values:
        return None
    return values[min(len(values) - 1, max(0, int(round(p / 100 * len(values) + .5)) - 1))]


def main():
    RESULTS.mkdir(parents=True, exist_ok=True)
    report = {"started_at": datetime.now(SAO_PAULO).isoformat()}
    session = ReaderSession()
    try:
        session.connect()
        ui = WebUI(session.driver, ReaderFailure, session.checkpoint, session.progress)
        log("Gabarito")
        chats, baseline = collect_baseline(session, ui)
        plan = choose_targets(baseline)
        log("Alvos:", len(plan), "de", len(baseline), "mensagens do gabarito")
        log("Bateria")
        rows = battery(session, plan, baseline)
        log("Controles negativos")
        negative = negatives(session, plan)
    finally:
        session.close()
    # A term common across chats (dates, file names), so a 5 s block leaves a cursor.
    term = "2026"
    log("Reinicios e retomada por cursor (termo frequente: %r)" % term)
    restart_steps = restarts(term)
    found = sum(r["found"] for r in rows)
    summary = {
        "targets": len(rows), "found": found, "recall": round(found / len(rows), 4) if rows else 0,
        "wrong_attribution": sum(r["wrong_attribution"] for r in rows),
        "partial_presented_as_complete": sum(1 for r in rows if r["status"] == "success" and not (r["coverage"] or {}).get("complete")),
        "by_mode": {mode: "%d/%d" % (sum(r["found"] for r in rows if r["mode"] == mode), sum(1 for r in rows if r["mode"] == mode)) for mode in ("global", "within", "within_period")},
        "by_type": {kind: "%d/%d" % (sum(r["found"] for r in rows if r["chat_type"] == kind), sum(1 for r in rows if r["chat_type"] == kind)) for kind in ("group", "contact")},
        "old_pages": "%d/%d" % (sum(r["found"] for r in rows if r["page"] > 0), sum(1 for r in rows if r["page"] > 0)),
        "first_progress_ms_max": max((r["first_progress_ms"] or 0) for r in rows) if rows else None,
        "first_batch_ms_p50": percentile([r["first_batch_ms"] for r in rows], 50),
        "first_batch_ms_p95": percentile([r["first_batch_ms"] for r in rows], 95),
        "elapsed_ms_p50": percentile([r["elapsed_ms"] for r in rows], 50),
        "elapsed_ms_p95": percentile([r["elapsed_ms"] for r in rows], 95),
        "negative_controls": "%d/%d" % (sum(n["passed"] for n in negative), len(negative)),
        "restarts_authenticated": sum(1 for s in restart_steps[1:] if s["status"] in ("success", "partial", "LOGGED_IN")),
        "restart_resumes": sum(1 for s in restart_steps if s.get("cursor_in") and s["status"] in ("success", "partial")),
        "restart_repeated_records": sum(s.get("repeated", 0) for s in restart_steps),
        "direction": direction_check(baseline),
        "timestamps_parsed": "%d/%d" % (sum(1 for m in baseline if m.get("timestamp")), len(baseline)),
    }
    summary["approved"] = (summary["recall"] >= .95 and summary["wrong_attribution"] == 0 and summary["partial_presented_as_complete"] == 0
                           and (summary["first_batch_ms_p95"] or 10 ** 9) <= 30000 and summary["first_progress_ms_max"] <= 2000
                           and summary["negative_controls"].split("/")[0] == summary["negative_controls"].split("/")[1]
                           and summary["restarts_authenticated"] >= 3 and summary["restart_repeated_records"] == 0
                           and summary["restart_resumes"] >= 1
                           and summary["direction"]["inconsistent"] == 0)
    report.update(finished_at=datetime.now(SAO_PAULO).isoformat(), summary=summary, baseline_chats=chats,
                  cases=rows, negatives=negative, restarts=restart_steps)
    OUTPUT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    log(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0 if summary["approved"] else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
