"""Score explicitly collected voice evaluations using JiWER, never homemade WER.

Input is local JSONL produced by FridaVoiceEvaluation.exe. No network or secrets.
"""
import argparse
from collections import defaultdict
import json
import math
from pathlib import Path
import statistics
import unicodedata


def normalize(value):
    # Keep accents, numbers and negations. Punctuation/case do not affect WER.
    return " ".join("".join(" " if unicodedata.category(c).startswith("P") else c
                            for c in value.lower()).split())


def summarize(rows):
    from jiwer import process_words
    if not rows:
        raise ValueError("Nenhuma tentativa para avaliar")
    seen, sessions, groups, coverage, identities = set(), {}, defaultdict(list), defaultdict(set), {}
    for row in rows:
        for field in ("id", "variant", "split", "session", "condition", "reference", "hypothesis",
                      "status", "operator_voice", "speaker_accepted", "elapsed_ms", "estimated_usd"):
            if field not in row:
                raise ValueError(f"Campo ausente: {field}")
        if row["split"] not in ("calibration", "test"):
            raise ValueError("Split invalido")
        key = (row["id"], row["variant"])
        if key in seen:
            raise ValueError(f"Tentativa duplicada: {key}")
        seen.add(key)
        identity = tuple(row[x] for x in ("reference", "split", "session", "condition", "operator_voice"))
        if identities.setdefault(row["id"], identity) != identity:
            raise ValueError("Mesmo ID tem referencias/condicoes diferentes entre candidatos")
        if sessions.setdefault(row["session"], row["split"]) != row["split"]:
            raise ValueError("Vazamento: mesma sessao em calibration e test")
        if not all(isinstance(row[x], str) for x in ("reference", "hypothesis")):
            raise ValueError("Referencia e hipotese devem ser texto")
        if not all(isinstance(row[x], bool) for x in ("operator_voice", "speaker_accepted")):
            raise ValueError("Rotulos de locutor devem ser booleanos")
        if not all(math.isfinite(row[x]) and row[x] >= 0 for x in ("elapsed_ms", "estimated_usd")):
            raise ValueError("Tempo/custo invalido")
        coverage[row["variant"]].add(row["id"])
        groups[(row["split"], row["variant"], "all")].append(row)
        groups[(row["split"], row["variant"], row["condition"])].append(row)
    paired = len({frozenset(ids) for ids in coverage.values()}) == 1
    report = []
    for (split, variant, condition), samples in sorted(groups.items()):
        positives = [r for r in samples if r["operator_voice"] and normalize(r["reference"])]
        negatives = [r for r in samples if not r["operator_voice"]]
        raw_ref = [normalize(r["reference"]) for r in positives]
        stt_hyp = [normalize(r["hypothesis"]) if r["status"] == "Ok" else "" for r in positives]
        gated_hyp = [hyp if r["speaker_accepted"] else "" for r, hyp in zip(positives, stt_hyp)]
        # Rejected operator samples stay in the denominator as deletions.
        stt = process_words(raw_ref, stt_hyp) if positives else None
        gated = process_words(raw_ref, gated_hyp) if positives else None
        critical = [r for r in positives if r.get("critical_terms")]
        critical_ok = sum(r["status"] == "Ok" and r["speaker_accepted"] and
                          all((" " + normalize(term) + " ") in (" " + normalize(r["hypothesis"]) + " ")
                              for term in r["critical_terms"]) for r in critical)
        latencies = sorted(r["elapsed_ms"] for r in samples)
        report.append({
            "split": split, "variant": variant, "condition": condition, "attempts": len(samples),
            "operator_samples": len(positives), "negative_samples": len(negatives),
            "wer_stt": stt.wer if stt else None,
            "wer_with_speaker_gate": gated.wer if gated else None,
            "exact_commands": sum(ref == hyp for ref, hyp in zip(raw_ref, gated_hyp)),
            "critical_samples": len(critical), "critical_all_present": critical_ok,
            "speaker_false_rejections": sum(not r["speaker_accepted"] for r in positives),
            "negative_accepted_texts": sum(r["status"] == "Ok" and r["speaker_accepted"] and bool(r["hypothesis"]) for r in negatives),
            "errors": sum(r["status"] in ("ServiceError", "Cancelled") for r in samples),
            "latency_median_ms": statistics.median(latencies),
            "latency_p95_ms": latencies[max(0, math.ceil(.95 * len(latencies)) - 1)],
            "estimated_usd": sum(r["estimated_usd"] for r in samples),
        })
    return {"schema_version": 1, "paired_same_ids": paired,
            "scope": "STT em audio original + decisao de locutor; nao certifica captura, wake, audio filtrado ou execucao",
            "acceptance": "pending_physical_validation",
            "warning": "Nenhum ganho e certificado automaticamente. Compare apenas grupos pareados e use test apos calibration. Termos presentes nao provam a intencao correta.",
            "groups": report}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path, nargs="+")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    rows = []
    for path in args.results:
        rows.extend(json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip())
    report = summarize(rows)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(args.output)


if __name__ == "__main__":
    main()
