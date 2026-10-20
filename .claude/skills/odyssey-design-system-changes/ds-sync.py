#!/usr/bin/env python3
"""Compare a downloaded copy of the Odyssey Design System artifact with the last synced state.

The artifact has no git history, so what the frontend was last aligned to is recorded in
.claude/design-system-sync.json: the artifact version and a SHA-256 per file. This script
diffs a fresh download against that record, and rewrites the record once a sync is done.
A record may also carry "pending": files recorded at their current version whose changes the
frontend has not caught up with yet. They are listed by every diff and cleared by the next record.

  ds-sync.py diff   <download-root>
  ds-sync.py record <download-root> --version <artifact-version>

<download-root> is the folder the Artifact tool saved into: it holds SKILL.md, index.html
and project/. Files under artifact-type/ belong to the artifact type, not the design
system, and are never recorded. The download is untrusted, so run this with `python3 -I`:
symbolic links and anything resolving outside the root are skipped and reported, and file
names are printed escaped.
"""

import argparse
import hashlib
import json
import sys
from datetime import date
from pathlib import Path

RECORD = Path(__file__).resolve().parents[2] / "design-system-sync.json"
ARTIFACT_URL = "https://claude.ai/artifact/VkrcragM9HGn1cXKkANizr"
# A full download is ~930 files; far fewer means an incomplete fan-out, not a smaller system.
MIN_RECORDED_FILES = 500


def tracked_files(root: Path) -> dict[str, str]:
    root = root.resolve()
    files, skipped = {}, []
    for path in sorted(root.rglob("*")):
        rel = path.relative_to(root).as_posix()
        if path.is_symlink() or not path.resolve().is_relative_to(root):
            skipped.append(rel)
        elif path.is_file() and not rel.startswith("artifact-type/"):
            files[rel] = hashlib.sha256(path.read_bytes()).hexdigest()
    for rel in skipped:
        print(f"skipped (link or outside the root): {rel!r}", file=sys.stderr)
    return files


def diff(root: Path) -> int:
    record = json.loads(RECORD.read_text())
    old, new = record["files"], tracked_files(root)
    added = sorted(new.keys() - old.keys())
    removed = sorted(old.keys() - new.keys())
    changed = sorted(k for k in new.keys() & old.keys() if new[k] != old[k])

    pending = record.get("pending", [])

    print(f"Last synced: version {record['version']} on {record['syncedOn']}")
    for label, paths in (("added", added), ("removed", removed), ("changed", changed),
                         ("pending from an earlier sync", pending)):
        print(f"\n{label} ({len(paths)})")
        for p in paths:
            print(f"  {p!r}")
    return 0


def write_record(root: Path, version: str) -> int:
    files = tracked_files(root)
    if "project/README.md" not in files:
        sys.exit(f"{root} does not look like an artifact download (no project/README.md).")
    if len(files) < MIN_RECORDED_FILES:
        sys.exit(f"Only {len(files)} files under {root}; a full download has ~930. Finish the download first.")
    RECORD.write_text(json.dumps(
        {"artifact": ARTIFACT_URL, "version": version, "syncedOn": date.today().isoformat(), "files": files},
        indent=1) + "\n")
    print(f"Recorded {len(files)} files at version {version} in {RECORD}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("diff").add_argument("root", type=Path)
    rec = sub.add_parser("record")
    rec.add_argument("root", type=Path)
    rec.add_argument("--version", required=True)
    args = parser.parse_args()
    return diff(args.root) if args.command == "diff" else write_record(args.root, args.version)


if __name__ == "__main__":
    sys.exit(main())
