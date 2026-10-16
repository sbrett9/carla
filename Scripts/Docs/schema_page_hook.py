"""The Claude Code hook that holds an edited schema page to its template.

Claude Code runs it after every Edit or Write, with the tool call as JSON on stdin. When the file is
a page under Docs/CAT_Research/Schemas (the README excepted), the page is checked against the
template with `check_schema_pages.py`, and every departure is printed with the template's name for
an older heading. Exit code 2 hands the findings back to the model as feedback on the edit; exit 0
is silence, for a page that fits and for every other file.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from check_schema_pages import NOT_PAGES, SCHEMAS, check_page  # noqa: E402


def main() -> int:
    try:
        call = json.load(sys.stdin)
    except (ValueError, OSError):
        return 0
    tool_input = call.get("tool_input") or {}
    raw = tool_input.get("file_path") or (call.get("tool_response") or {}).get("filePath")
    if not raw:
        return 0
    path = Path(raw)
    try:
        path = path.resolve()
    except OSError:
        return 0
    if path.suffix != ".md" or path.name in NOT_PAGES or path.parent != SCHEMAS.resolve():
        return 0
    if not path.is_file():
        return 0
    findings = check_page(path)
    if not findings:
        return 0
    # Claude Code relays a blocking hook's stderr to the model, not its stdout.
    print(f"schema-page: {path.name} departs from the page template in {len(findings)} place(s); "
          "the rules are Docs/CAT_Research/Schemas/AGENTS.md in the carla repository",
          file=sys.stderr)
    for line, what in findings:
        print(f"  line {line}: {what}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
