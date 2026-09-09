#!/usr/bin/env python3
"""Create an auditable shortlist of group/build claims from normalized transcripts.

This produces research candidates, not database mutations. Automatic captions are
noisy and a nearby group mention is not proof of ownership. Every hit retains a
timestamped source URL and transcript context for human review.
"""

from __future__ import annotations

import argparse
import json
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


GROUPS: dict[str, tuple[str, ...]] = {
    "The Imperials": (r"\bthe imperials\b", r"\bimperials\b"),
    "The Emperium": (r"\bthe emperium\b", r"\bemperium\b"),
    "Team Veteran": (r"\bteam veteran(?:s)?\b", r"\bveteran(?:s)?\b"),
    "Team Rusher": (r"\bteam rusher(?:s)?\b", r"\brusher(?:s)?\b"),
    "SpawnMasons": (r"\bspawn masons?\b", r"\bspawnmasons?\b"),
    "Highway Workers Union": (r"\bhighway workers union\b", r"\bhwu\b"),
    "Southern Canal Corps": (r"\bsouthern canal corps\b",),
    "Southern Canal Association": (r"\bsouthern canal association\b",),
    "WaterWay Union": (r"\bwaterway union\b",),
    "Nether Highway Group": (r"\bnether highway group\b", r"\bnhg\b"),
    "DonFuer": (r"\bdon\s*fuer\b",),
}

CLAIM_RE = re.compile(
    r"(?i)\b(?:built|build|builder|construct(?:ed|ion)?|founded|created|made|"
    r"expanded|maintain(?:ed|ing)?|repaired|designed|member(?:s)?|owned|base|"
    r"city|town|canal|highway|project|outpost|lodge)\b"
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=r"C:\AtlasExample\Research\2b2t-youtube")
    parser.add_argument(
        "--output",
        default=r"C:\AtlasExample\Research\2b2t-youtube\attribution-candidates.json",
    )
    parser.add_argument("--context", type=int, default=3)
    return parser.parse_args()


def stamp(milliseconds: int) -> str:
    seconds = milliseconds // 1000
    return f"{seconds // 3600:02d}:{(seconds % 3600) // 60:02d}:{seconds % 60:02d}"


def timestamp_url(url: str, milliseconds: int) -> str:
    separator = "&" if "?" in url else "?"
    return f"{url}{separator}t={milliseconds // 1000}s"


def main() -> int:
    args = parse_args()
    root = Path(args.root).resolve()
    output = Path(args.output).resolve()
    records = sorted(root.glob("*/normalized/*.json"))
    candidates: list[dict[str, Any]] = []
    compiled = {
        group: tuple(re.compile(pattern, re.IGNORECASE) for pattern in patterns)
        for group, patterns in GROUPS.items()
    }

    for path in records:
        record = json.loads(path.read_text(encoding="utf-8"))
        segments = record.get("segments") or []
        for index, segment in enumerate(segments):
            text = str(segment.get("text") or "")
            groups = [group for group, patterns in compiled.items()
                      if any(pattern.search(text) for pattern in patterns)]
            if not groups:
                continue
            start = max(0, index - args.context)
            end = min(len(segments), index + args.context + 1)
            context = " ".join(str(item.get("text") or "") for item in segments[start:end])
            start_ms = int(segment.get("startMs") or 0)
            candidates.append({
                "groups": groups,
                "evidenceLevel": "claim-context" if CLAIM_RE.search(context) else "mention-only",
                "channel": record.get("channel"),
                "videoId": record.get("videoId"),
                "title": record.get("title"),
                "uploadDate": record.get("uploadDate"),
                "timestamp": stamp(start_ms),
                "url": timestamp_url(str(record.get("url")), start_ms),
                "matchedText": text,
                "context": context,
                "sourceFile": str(path),
            })

    report = {
        "schema": "atlas.youtube-attribution-candidates.v1",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "policy": "Research shortlist only. Review and corroborate before changing Atlas data.",
        "transcriptsScanned": len(records),
        "candidateCount": len(candidates),
        "candidates": candidates,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    markdown = output.with_suffix(".md")
    lines = [
        "# YouTube attribution candidates", "",
        f"Scanned {len(records)} normalized transcripts; found {len(candidates)} review candidates.", "",
        "> Research shortlist only. Review and corroborate before changing Atlas data.", "",
    ]
    for candidate in candidates:
        lines.extend([
            f"## {', '.join(candidate['groups'])} — {candidate['title']}", "",
            f"- Evidence class: `{candidate['evidenceLevel']}`",
            f"- Source: [{candidate['channel']} at {candidate['timestamp']}]({candidate['url']})",
            f"- Match: {candidate['matchedText']}",
            f"- Context: {candidate['context']}", "",
        ])
    markdown.write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"transcripts": len(records), "candidates": len(candidates), "output": str(output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
