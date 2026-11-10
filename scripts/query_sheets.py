# language: Python, file: query_sheets.py, runtime: Python 3.12, target: Windows 11
import argparse
import csv
import io
import json
import sys
import urllib.error
import urllib.request

SHEETS = [
    {
        "label": "Planilha_1",
        "id": "1sD7RnNjHBCt4TWWZizldaxi20WS7gjVmZKKEn8796PQ",
        "gid": "1047389234",
    },
    {
        "label": "Planilha_2",
        "id": "1T2KTKIXBi-oLYmev2YTGswJbtc5M21szu_5jPn6FqXE",
        "gid": "0",
    },
    {
        "label": "Planilha_3",
        "id": "1XG9NVT-xPbPYDLN-gbFkYXCiqmLK3RCvVj3n9ftHee0",
        "gid": "0",
    },
]


def fetch_csv_rows(sheet_id: str, gid: str) -> tuple[str, list[list[str]]]:
    url = f"https://docs.google.com/spreadsheets/d/{sheet_id}/export?format=csv&gid={gid}"
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            final_url = resp.geturl()
            body = resp.read().decode("utf-8-sig", errors="replace")
            if "accounts.google.com" in final_url or body.lstrip().startswith("<!DOCTYPE html"):
                return "auth_required", []
            reader = csv.reader(io.StringIO(body))
            rows = [r for r in reader if any(cell.strip() for cell in r)]
            return "ok", rows
    except urllib.error.HTTPError as exc:
        if exc.code in (401, 403):
            return "auth_required", []
        return f"http_{exc.code}", []
    except Exception as exc:
        return f"error_{type(exc).__name__}", []


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Consulta somente-leitura nas 3 planilhas base")
    parser.add_argument("--search", default="", help="Termo para buscar em todas as linhas")
    parser.add_argument("--preview", type=int, default=10, help="Quantidade de linhas na visualizacao inicial")
    parser.add_argument("--limit", type=int, default=40, help="Maximo de ocorrencias por planilha")
    args = parser.parse_args()

    needle = args.search.strip().casefold()
    report = []
    for item in SHEETS:
        status, rows = fetch_csv_rows(item["id"], item["gid"])
        entry: dict = {
            "label": item["label"],
            "spreadsheet_id": item["id"],
            "gid": item["gid"],
            "status": status,
            "total_non_empty_rows": len(rows),
        }
        if status == "ok" and rows:
            entry["headers"] = rows[0]
            if needle:
                matches = []
                for idx, row in enumerate(rows[1:], start=2):
                    joined = " | ".join(row).casefold()
                    if needle in joined:
                        matches.append({"row_number": idx, "values": row})
                        if len(matches) >= args.limit:
                            break
                entry["match_count"] = len(matches)
                entry["matches"] = matches
            else:
                entry["preview_rows"] = rows[1 : 1 + args.preview]
        report.append(entry)

    print(json.dumps({"read_only": True, "checked_all_three": True, "results": report}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

