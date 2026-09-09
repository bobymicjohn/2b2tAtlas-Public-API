#!/usr/bin/env python3
"""Match timestamped YouTube research captions to live Atlas locations.

The output is a review queue, never a database mutation. Location names that are
short or generic are deliberately excluded, transcript-only hits require nearby
historical language for the useful queue, and every candidate retains the exact
timestamp and context that caused the match.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import unicodedata
import urllib.request
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


DEFAULT_LOCATIONS_URL = "http://127.0.0.1:5297/api/locations"
DEFAULT_ROOT = r"C:\AtlasExample\Research\2b2t-youtube"
DEFAULT_OUTPUT = r"C:\AtlasExample\Research\2b2t-youtube\location-candidates.json"
USER_AGENT = "2b2tAtlas YouTube location audit/1.0 (review-only)"

GENERIC_SINGLE_TOKENS = {
    "2b2t", "archive", "base", "bridge", "canal", "castle", "city", "end",
    "farm", "highway", "hotel", "island", "lodge", "map", "museum", "nether",
    "outpost", "spawn", "stash", "station", "temple", "town", "village", "world",
}
GENERIC_PHRASES = {
    "old base", "old spawn", "new spawn", "world border", "nether spawn",
    "spawn base", "spawn city", "end portal", "the end", "the highway", "the archive",
}
CLAIM_RE = re.compile(
    r"\b(?:built|build|builder|construct(?:ed|ion)?|founded|created|made|expanded|"
    r"maintain(?:ed|ing)?|repaired|designed|member(?:s)?|owner|owned|resident|"
    r"grief(?:ed|ing)?|destroyed|survived|discovered|history|located|coordinates|"
    r"base|city|town|canal|highway|project|outpost|lodge|visit(?:ed|ing)?|tour|show(?:n|ing)?)\b",
    re.IGNORECASE,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=DEFAULT_ROOT)
    parser.add_argument("--locations-url", default=DEFAULT_LOCATIONS_URL)
    parser.add_argument("--locations-json", help="Use a cached locations response instead of HTTP.")
    parser.add_argument("--output", default=DEFAULT_OUTPUT)
    parser.add_argument("--context", type=int, default=4)
    parser.add_argument("--include-mentions", action="store_true",
                        help="Include weak transcript mentions as well as title/claim evidence.")
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def normalized_words(value: str | None) -> str:
    if not value:
        return ""
    value = unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode("ascii")
    return re.sub(r"\s+", " ", re.sub(r"[^a-z0-9]+", " ", value.casefold())).strip()


def usable_alias(name: str) -> str | None:
    alias = normalized_words(name)
    tokens = alias.split()
    if not alias or alias in GENERIC_PHRASES:
        return None
    if len(tokens) == 1 and (len(alias) < 7 or alias in GENERIC_SINGLE_TOKENS):
        return None
    if len(tokens) > 1 and len(alias.replace(" ", "")) < 6:
        return None
    return alias


def phrase_present(text: str, phrase: str) -> bool:
    # Both arguments are normalized_words output. Literal token boundaries avoid
    # compiling a regex for every location against every caption segment.
    return f" {phrase} " in f" {text} "


def timestamp(milliseconds: int) -> str:
    seconds = max(0, milliseconds // 1000)
    return f"{seconds // 3600:02d}:{(seconds % 3600) // 60:02d}:{seconds % 60:02d}"


def timestamp_url(url: str, milliseconds: int) -> str:
    separator = "&" if "?" in url else "?"
    return f"{url}{separator}t={max(0, milliseconds // 1000)}s"


def load_locations(args: argparse.Namespace) -> list[dict[str, Any]]:
    if args.locations_json:
        return json.loads(Path(args.locations_json).read_text(encoding="utf-8"))
    request = urllib.request.Request(args.locations_url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=45) as response:
        return json.load(response)


def existing_source_urls(location: dict[str, Any]) -> set[str]:
    urls = {str(location.get("videoUrl") or "").strip()}
    for attachment in location.get("attachments") or []:
        urls.update(str(attachment.get(key) or "").strip()
                    for key in ("path", "sourceUrl"))
    return {url for url in urls if url}


def context_for(segments: list[dict[str, Any]], index: int, radius: int) -> str:
    start = max(0, index - radius)
    end = min(len(segments), index + radius + 1)
    return " ".join(str(item.get("text") or "") for item in segments[start:end]).strip()


def candidate_score(title_match: bool, claim_context: bool, occurrences: int) -> tuple[str, float]:
    if title_match and claim_context:
        return "title-and-claim", 0.98
    if title_match:
        return "title", 0.93
    if claim_context and occurrences > 1:
        return "repeated-claim-context", 0.84
    if claim_context:
        return "claim-context", 0.76
    return "mention-only", 0.5


def single_token_entity_context(context: str, alias: str) -> bool:
    normalized = normalized_words(context)
    entity = r"(?:base|city|town|lodge|outpost|project|settlement|location|called|named)"
    return bool(re.search(
        rf"(?:{entity}\s+(?:the\s+)?{re.escape(alias)}|{re.escape(alias)}\s+{entity})(?:$|\s)",
        normalized,
    ))


def single_token_title_context(title: str, alias: str) -> bool:
    framing = r"(?:history of|truth about|story of|tour of|fall of|rise of|mystery of|current condition of|base called|base named|base at|city of|town of)"
    return bool(re.search(rf"(?:^|\s){framing}\s+(?:the\s+)?{re.escape(alias)}(?:$|\s)", title))


def extract_catalog_title_candidates(
    locations: list[dict[str, Any]],
    catalogs: Iterable[Path],
    existing_keys: set[tuple[int, str]],
) -> list[dict[str, Any]]:
    provisional = [(location, usable_alias(str(location.get("name") or ""))) for location in locations]
    alias_counts: dict[str, int] = defaultdict(int)
    for _, alias in provisional:
        if alias:
            alias_counts[alias] += 1
    aliases = [(location, alias) for location, alias in provisional
               if alias and alias_counts[alias] == 1]
    results: list[dict[str, Any]] = []

    for path in catalogs:
        catalog = json.loads(path.read_text(encoding="utf-8"))
        channel = catalog.get("channel") or path.parent.name
        for entry in catalog.get("entries") or []:
            video_id = str(entry.get("id") or "")
            video_url = str(entry.get("url") or (f"https://www.youtube.com/watch?v={video_id}" if video_id else ""))
            title = normalized_words(str(entry.get("title") or ""))
            if not video_id or not title:
                continue
            for location, alias in aliases:
                if not phrase_present(title, alias):
                    continue
                framed = single_token_title_context(title, alias)
                exact = title == alias
                typed = bool(re.search(
                    rf"(?:^|\s){re.escape(alias)}\s+(?:base|city|town|lodge|outpost|project)(?:$|\s)", title))
                if not (framed or exact or typed):
                    continue
                location_id = int(location.get("rowid") or 0)
                key = (location_id, video_id)
                if key in existing_keys:
                    continue
                already_linked = video_url in existing_source_urls(location)
                results.append({
                    "locationId": location_id,
                    "locationName": location.get("name"),
                    "dimension": location.get("dimensionName"),
                    "evidenceClass": "catalog-title",
                    "confidence": 0.92,
                    "titleMatch": True,
                    "transcriptOccurrences": 0,
                    "channel": channel,
                    "videoId": video_id,
                    "videoTitle": entry.get("title"),
                    "uploadDate": None,
                    "videoUrl": video_url,
                    "alreadyLinked": already_linked,
                    "needsAttachment": not already_linked,
                    "reviewPriority": "high",
                    "moments": [],
                    "sourceFile": str(path),
                })
                existing_keys.add(key)
    return results


def extract_candidates(
    locations: list[dict[str, Any]],
    records: Iterable[Path],
    context_radius: int,
    include_mentions: bool,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    provisional_aliases: list[tuple[dict[str, Any], str]] = []
    skipped: list[dict[str, Any]] = []
    for location in locations:
        alias = usable_alias(str(location.get("name") or ""))
        if alias:
            provisional_aliases.append((location, alias))
        else:
            skipped.append({"locationId": location.get("rowid"), "name": location.get("name"),
                            "reason": "short-or-generic-name"})

    alias_counts = Counter(alias for _, alias in provisional_aliases)
    location_aliases: list[tuple[dict[str, Any], str]] = []
    for location, alias in provisional_aliases:
        if alias_counts[alias] > 1:
            skipped.append({"locationId": location.get("rowid"), "name": location.get("name"),
                            "reason": "duplicate-location-name", "normalizedName": alias,
                            "duplicateCount": alias_counts[alias]})
        else:
            location_aliases.append((location, alias))

    candidates: list[dict[str, Any]] = []
    for path in records:
        record = json.loads(path.read_text(encoding="utf-8"))
        title = normalized_words(str(record.get("title") or ""))
        segments = record.get("segments") or []
        normalized_segments = [normalized_words(str(segment.get("text") or "")) for segment in segments]
        source_url = str(record.get("url") or "")

        for location, alias in location_aliases:
            title_match = phrase_present(title, alias)
            single_token = " " not in alias
            if title_match and single_token:
                title_match = single_token_title_context(title, alias)
            hit_indexes = [index for index, text in enumerate(normalized_segments)
                           if phrase_present(text, alias)]
            if single_token:
                hit_indexes = [index for index in hit_indexes
                               if single_token_entity_context(context_for(segments, index, context_radius), alias)]
            if not title_match and not hit_indexes:
                continue

            moments: list[dict[str, Any]] = []
            claim_context = False
            for index in hit_indexes[:5]:
                start_ms = int(segments[index].get("startMs") or 0)
                context = context_for(segments, index, context_radius)
                is_claim = bool(CLAIM_RE.search(context))
                claim_context = claim_context or is_claim
                moments.append({
                    "timestamp": timestamp(start_ms),
                    "url": timestamp_url(source_url, start_ms),
                    "matchedText": segments[index].get("text"),
                    "context": context,
                    "claimLanguageNearby": is_claim,
                })

            evidence_class, score = candidate_score(title_match, claim_context, len(hit_indexes))
            if evidence_class == "mention-only" and not include_mentions:
                continue
            already_linked = source_url in existing_source_urls(location)
            candidates.append({
                "locationId": location.get("rowid"),
                "locationName": location.get("name"),
                "dimension": location.get("dimensionName"),
                "evidenceClass": evidence_class,
                "confidence": score,
                "titleMatch": title_match,
                "transcriptOccurrences": len(hit_indexes),
                "channel": record.get("channel"),
                "videoId": record.get("videoId"),
                "videoTitle": record.get("title"),
                "uploadDate": record.get("uploadDate"),
                "videoUrl": source_url,
                "alreadyLinked": already_linked,
                "needsAttachment": not already_linked,
                "reviewPriority": ("high" if score >= 0.9 else "medium" if score >= 0.84 else "low"),
                "moments": moments,
                "sourceFile": str(path),
            })

    candidates.sort(key=lambda item: (-float(item["confidence"]), str(item["locationName"]).casefold(),
                                      str(item["uploadDate"] or "")))
    return candidates, skipped


def run_self_test() -> int:
    assertions = {
        "generic spawn rejected": usable_alias("Spawn") is None,
        "generic world border rejected": usable_alias("World Border") is None,
        "distinct name accepted": usable_alias("Space Valkyria") == "space valkyria",
        "short unique rejected": usable_alias("Nu") is None,
        "generic end portal rejected": usable_alias("End Portal") is None,
        "phrase boundaries": phrase_present("history of space valkyria on 2b2t", "space valkyria"),
        "no substring match": not phrase_present("summermelons history", "summermelon"),
        "one-word title framed": single_token_title_context("2b2t the history of kaamtown", "kaamtown"),
        "multi-word title framed": single_token_title_context(
            "2b2t the truth about block game mecca", "block game mecca"),
        "one-word ordinary title rejected": not single_token_title_context(
            "how 2b2t changed a players life forever", "forever"),
    }
    failed = [name for name, passed in assertions.items() if not passed]
    print(json.dumps({"tests": len(assertions), "failed": failed}))
    return 1 if failed else 0


def main() -> int:
    args = parse_args()
    if args.self_test:
        return run_self_test()

    root = Path(args.root).resolve()
    output = Path(args.output).resolve()
    records = sorted(root.glob("*/normalized/*.json"))
    catalogs = sorted(root.glob("*/catalog.json"))
    locations = load_locations(args)
    candidates, skipped = extract_candidates(
        locations, records, max(1, args.context), args.include_mentions)
    existing_keys = {(int(item["locationId"]), str(item["videoId"])) for item in candidates}
    candidates.extend(extract_catalog_title_candidates(locations, catalogs, existing_keys))
    candidates.sort(key=lambda item: (-float(item["confidence"]), str(item["locationName"]).casefold(),
                                      str(item["videoTitle"] or "").casefold()))

    report = {
        "schema": "atlas.youtube-location-candidates.v1",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "policy": "Review-only research queue. Corroborate claims before changing Atlas data.",
        "locationsScanned": len(locations),
        "transcriptsScanned": len(records),
        "channelCatalogsScanned": len(catalogs),
        "candidateCount": len(candidates),
        "unlinkedCandidateCount": sum(1 for item in candidates if item["needsAttachment"]),
        "skippedLocationCount": len(skipped),
        "skippedLocations": skipped,
        "candidates": candidates,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    markdown = output.with_suffix(".md")
    lines = [
        "# YouTube location candidates", "",
        f"Scanned {len(records)} transcripts against {len(locations)} live Atlas locations; "
        f"found {len(candidates)} review candidates.", "",
        "> Review-only. Captions can be inaccurate and a mention is not proof of ownership.", "",
    ]
    for item in candidates:
        status = "already linked" if item["alreadyLinked"] else "attachment candidate"
        lines.extend([
            f"## {item['locationName']} — {item['videoTitle']}", "",
            f"- Atlas location: `{item['locationId']}` ({item['dimension']})",
            f"- Evidence: `{item['evidenceClass']}` ({item['confidence']:.0%}); {status}",
            f"- Video: [{item['channel']}]({item['videoUrl']})",
        ])
        for moment in item["moments"][:3]:
            lines.append(f"- [{moment['timestamp']}]({moment['url']}): {moment['context']}")
        lines.append("")
    markdown.write_text("\n".join(lines), encoding="utf-8")

    print(json.dumps({
        "locations": len(locations), "transcripts": len(records),
        "catalogs": len(catalogs),
        "candidates": len(candidates),
        "unlinked": report["unlinkedCandidateCount"], "output": str(output),
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
