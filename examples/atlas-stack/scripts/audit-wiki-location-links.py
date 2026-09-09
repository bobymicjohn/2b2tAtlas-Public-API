#!/usr/bin/env python3
"""Find strong 2b2t Wiki article candidates for Atlas locations lacking a wiki URL.

This is intentionally review-only. It uses exact, punctuation-normalized, and
explicit Roman/numeric iteration matches; it does not use fuzzy similarity.
"""

from __future__ import annotations

import argparse
import json
import re
import time
import unicodedata
import urllib.parse
import urllib.request
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_API = "https://2b2t.miraheze.org/w/api.php"
DEFAULT_PUBLIC_WIKI = "https://2b2t.wikioasis.org/wiki/"
DEFAULT_LOCATIONS_URL = "http://127.0.0.1:5297/api/locations"
DEFAULT_OUTPUT = r"C:\AtlasExample\Research\2b2t-wiki-media\location-link-candidates.json"
USER_AGENT = "2b2tAtlas wiki location-link audit/1.0 (review-only; atlas.example)"
ROMAN_SUFFIXES = {
    "i": "1", "ii": "2", "iii": "3", "iv": "4", "v": "5",
    "vi": "6", "vii": "7", "viii": "8", "ix": "9", "x": "10",
}
GENERIC_NAMES = {
    "base", "city", "end portal", "highway", "nether", "new spawn", "old base",
    "old spawn", "pit fight", "spawn", "spawn base", "stash", "the end", "town", "world border",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api", default=DEFAULT_API)
    parser.add_argument("--public-wiki", default=DEFAULT_PUBLIC_WIKI)
    parser.add_argument("--locations-url", default=DEFAULT_LOCATIONS_URL)
    parser.add_argument("--locations-json")
    parser.add_argument("--output", default=DEFAULT_OUTPUT)
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def api_get(api: str, params: dict[str, str], attempts: int = 4) -> dict[str, Any]:
    query = urllib.parse.urlencode({"format": "json", "formatversion": "2", **params})
    request = urllib.request.Request(f"{api}?{query}", headers={"User-Agent": USER_AGENT})
    for attempt in range(attempts):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.load(response)
        except Exception:
            if attempt + 1 == attempts:
                raise
            time.sleep(1.5 * (attempt + 1))
    raise RuntimeError("unreachable")


def load_locations(args: argparse.Namespace) -> list[dict[str, Any]]:
    if args.locations_json:
        return json.loads(Path(args.locations_json).read_text(encoding="utf-8"))
    request = urllib.request.Request(args.locations_url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=45) as response:
        return json.load(response)


def all_articles(api: str) -> list[dict[str, Any]]:
    articles: list[dict[str, Any]] = []
    continuation: dict[str, str] = {}
    while True:
        payload = api_get(api, {
            "action": "query", "list": "allpages", "apnamespace": "0",
            "aplimit": "max", "apfilterredir": "all", **continuation,
        })
        articles.extend(payload.get("query", {}).get("allpages", []))
        continuation = payload.get("continue", {})
        if not continuation:
            return articles


def latest_revisions(api: str, page_ids: list[int]) -> dict[int, dict[str, Any]]:
    """Return one pinned revision identity per candidate page in bounded API batches."""
    revisions: dict[int, dict[str, Any]] = {}
    unique_ids = sorted({page_id for page_id in page_ids if page_id > 0})
    for offset in range(0, len(unique_ids), 50):
        batch = unique_ids[offset:offset + 50]
        payload = api_get(api, {
            "action": "query", "prop": "revisions",
            "pageids": "|".join(str(page_id) for page_id in batch),
            "rvprop": "ids|timestamp",
        })
        if payload.get("error"):
            error = payload["error"]
            raise RuntimeError(f"MediaWiki revision query failed: {error.get('code')}: {error.get('info')}")
        for page in payload.get("query", {}).get("pages", []):
            page_revisions = page.get("revisions") or []
            if not page_revisions:
                continue
            revision = page_revisions[0]
            revisions[int(page["pageid"])] = {
                "revisionId": revision.get("revid"),
                "revisionTimestamp": revision.get("timestamp"),
            }
    return revisions


def normalized_words(value: str | None) -> str:
    if not value:
        return ""
    value = unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode("ascii")
    return re.sub(r"\s+", " ", re.sub(r"[^a-z0-9]+", " ", value.casefold())).strip()


def normalized_compact(value: str | None) -> str:
    return normalized_words(value).replace(" ", "")


def normalized_iteration(value: str | None) -> str:
    tokens = normalized_words(value).split()
    if tokens and tokens[-1] in ROMAN_SUFFIXES:
        tokens[-1] = ROMAN_SUFFIXES[tokens[-1]]
    return "".join(tokens)


def usable_name(name: str) -> bool:
    normalized = normalized_words(name)
    return bool(normalized and normalized not in GENERIC_NAMES and len(normalized.replace(" ", "")) >= 5)


def public_url(root: str, title: str) -> str:
    return root.rstrip("/") + "/" + urllib.parse.quote(title.replace(" ", "_"), safe="_()'-")


def revision_url(page_url: str, revision_id: int | None) -> str | None:
    if not revision_id:
        return None
    return page_url + ("&" if "?" in page_url else "?") + "oldid=" + str(revision_id)


def run_self_test() -> int:
    tests = {
        "punctuation normalization": normalized_compact("Krobar's Bridge") == "krobarsbridge",
        "Roman iteration": normalized_iteration("Fusionia I") == normalized_iteration("Fusionia 1"),
        "iteration preserved": normalized_iteration("Fusionia II") != normalized_iteration("Fusionia I"),
        "generic rejected": not usable_name("End Portal"),
        "generic stash rejected": not usable_name("Stash"),
        "specific accepted": usable_name("Interdimensional Bridge"),
        "public URL": public_url(DEFAULT_PUBLIC_WIKI, "Imperator's Base").endswith("Imperator's_Base"),
        "revision URL": revision_url("https://example.test/wiki/A", 42) == "https://example.test/wiki/A?oldid=42",
    }
    failed = [name for name, passed in tests.items() if not passed]
    print(json.dumps({"tests": len(tests), "failed": failed}))
    return 1 if failed else 0


def main() -> int:
    args = parse_args()
    if args.self_test:
        return run_self_test()

    locations = load_locations(args)
    articles = all_articles(args.api)
    exact: dict[str, list[dict[str, Any]]] = defaultdict(list)
    compact: dict[str, list[dict[str, Any]]] = defaultdict(list)
    iterations: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for article in articles:
        title = str(article.get("title") or "")
        exact[normalized_words(title)].append(article)
        compact[normalized_compact(title)].append(article)
        iterations[normalized_iteration(title)].append(article)

    name_counts = Counter(normalized_compact(str(location.get("name") or "")) for location in locations)
    candidates: list[dict[str, Any]] = []
    skipped: list[dict[str, Any]] = []
    for location in locations:
        name = str(location.get("name") or "")
        compact_name = normalized_compact(name)
        if location.get("wiki"):
            continue
        if not usable_name(name):
            skipped.append({"locationId": location.get("rowid"), "name": name, "reason": "short-or-generic-name"})
            continue
        if name_counts[compact_name] > 1:
            skipped.append({"locationId": location.get("rowid"), "name": name, "reason": "duplicate-location-name"})
            continue

        matches: list[dict[str, Any]] = []
        evidence = ""
        confidence = 0.0
        word_name = normalized_words(name)
        if exact.get(word_name):
            matches, evidence, confidence = exact[word_name], "exact-title", 0.99
        elif compact.get(compact_name):
            matches, evidence, confidence = compact[compact_name], "punctuation-normalized-title", 0.95
        else:
            iteration_name = normalized_iteration(name)
            if iterations.get(iteration_name):
                matches, evidence, confidence = iterations[iteration_name], "explicit-iteration-title", 0.91

        if len(matches) != 1:
            if len(matches) > 1:
                skipped.append({"locationId": location.get("rowid"), "name": name,
                                "reason": "ambiguous-wiki-title", "titles": [item.get("title") for item in matches]})
            continue
        article = matches[0]
        title = str(article.get("title"))
        candidates.append({
            "locationId": location.get("rowid"),
            "locationName": name,
            "dimension": location.get("dimensionName"),
            "articleTitle": title,
            "wikiPageId": article.get("pageid"),
            "wikiUrl": public_url(args.public_wiki, title),
            "evidenceClass": evidence,
            "confidence": confidence,
            "reviewPriority": "high" if confidence >= 0.95 else "medium",
        })

    revisions = latest_revisions(args.api, [int(item["wikiPageId"]) for item in candidates])
    for item in candidates:
        revision = revisions.get(int(item["wikiPageId"]), {})
        item.update(revision)
        item["revisionUrl"] = revision_url(item["wikiUrl"], revision.get("revisionId"))

    candidates.sort(key=lambda item: (-float(item["confidence"]), str(item["locationName"]).casefold()))
    output = Path(args.output).resolve()
    report = {
        "schema": "atlas.wiki-location-link-candidates.v2",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "policy": "Review-only exact-name queue. Verify article identity before changing Atlas data.",
        "locationsScanned": len(locations),
        "wikiArticlesScanned": len(articles),
        "candidateCount": len(candidates),
        "skipped": skipped,
        "candidates": candidates,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    markdown = output.with_suffix(".md")
    lines = [
        "# 2b2t Wiki location-link candidates", "",
        f"Scanned {len(articles)} wiki articles against {len(locations)} live Atlas locations; "
        f"found {len(candidates)} strong review candidates.", "",
        "> Review-only. Exact names can still refer to different entities.", "",
    ]
    for item in candidates:
        lines.extend([
            f"## {item['locationName']}", "",
            f"- Match: `{item['evidenceClass']}` ({item['confidence']:.0%})",
            f"- Atlas: location `{item['locationId']}` ({item['dimension']})",
            f"- Wiki: [{item['articleTitle']}]({item['wikiUrl']})", "",
            f"- Pinned source: [revision {item.get('revisionId')}]({item.get('revisionUrl')}) "
            f"at `{item.get('revisionTimestamp')}`", "",
        ])
    markdown.write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"locations": len(locations), "articles": len(articles),
                      "candidates": len(candidates), "output": str(output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
