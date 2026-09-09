#!/usr/bin/env python3
"""Inventory media on Atlas-linked 2b2t Wiki pages for human review.

Only locations that already carry a wiki URL are queried, making page identity much
stronger than fuzzy title matching. Results include the original file page and
license metadata. This script never downloads media and never mutates Atlas data.
"""

from __future__ import annotations

import argparse
import html
import json
import re
import time
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_API = "https://2b2t.miraheze.org/w/api.php"
DEFAULT_LOCATIONS_URL = "http://127.0.0.1:5297/api/locations"
DEFAULT_OUTPUT = r"C:\AtlasExample\Research\2b2t-wiki-media\media-candidates.json"
USER_AGENT = "2b2tAtlas wiki location media audit/1.0 (review-only; atlas.example)"
REUSE_AUTHORIZATION = (
    "Direct reuse permission confirmed September 4, 2026 by Joey_Coconut, "
    "2b2t Wiki administrator and 2b2t Atlas team member."
)
MEDIA_EXTENSIONS = (".png", ".jpg", ".jpeg", ".webp", ".gif")
LOW_VALUE_RE = re.compile(r"(?i)\b(?:download|logo|icon|banner|wordmark|favicon|placeholder)\b")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api", default=DEFAULT_API)
    parser.add_argument("--locations-url", default=DEFAULT_LOCATIONS_URL)
    parser.add_argument("--locations-json")
    parser.add_argument("--output", default=DEFAULT_OUTPUT)
    parser.add_argument("--max-locations", type=int, default=0)
    parser.add_argument("--delay-ms", type=int, default=100)
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


def wiki_title(url: str | None) -> str | None:
    if not url:
        return None
    parsed = urllib.parse.urlparse(url)
    match = re.search(r"/wiki/(.+)$", parsed.path)
    if not match:
        query = urllib.parse.parse_qs(parsed.query)
        candidate = (query.get("title") or [None])[0]
    else:
        candidate = match.group(1)
    return urllib.parse.unquote(candidate).replace("_", " ").strip() if candidate else None


def flatten_metadata(value: Any) -> str | None:
    if isinstance(value, dict):
        value = value.get("value")
    if value is None:
        return None
    cleaned = re.sub(r"<[^>]+>", " ", html.unescape(str(value)))
    cleaned = re.sub(r"\s+", " ", cleaned).strip()
    return cleaned or None


def page_media(api: str, title: str) -> tuple[int | None, int | None, list[str]]:
    images: set[str] = set()
    continuation: dict[str, str] = {}
    page_id: int | None = None
    revision_id: int | None = None
    while True:
        payload = api_get(api, {
            "action": "query", "prop": "images|revisions", "titles": title,
            "imlimit": "max", "rvprop": "ids", "rvlimit": "1", **continuation,
        })
        pages = payload.get("query", {}).get("pages", [])
        if not pages or pages[0].get("missing"):
            return None, None, []
        page = pages[0]
        page_id = page.get("pageid")
        revisions = page.get("revisions") or []
        revision_id = revisions[0].get("revid") if revisions else revision_id
        images.update(str(item.get("title")) for item in page.get("images") or [] if item.get("title"))
        continuation = payload.get("continue", {})
        if not continuation:
            break
    return page_id, revision_id, sorted(images, key=str.casefold)


def image_info(api: str, titles: list[str]) -> dict[str, dict[str, Any]]:
    results: dict[str, dict[str, Any]] = {}
    for offset in range(0, len(titles), 40):
        payload = api_get(api, {
            "action": "query", "prop": "imageinfo", "titles": "|".join(titles[offset:offset + 40]),
            "iiprop": "url|size|mime|extmetadata", "iiurlwidth": "960",
        })
        for page in payload.get("query", {}).get("pages", []):
            info = (page.get("imageinfo") or [{}])[0]
            results[str(page.get("title"))] = info
    return results


def existing_urls(location: dict[str, Any]) -> set[str]:
    values: set[str] = set()
    for attachment in location.get("attachments") or []:
        values.update(str(attachment.get(key) or "").strip()
                      for key in ("path", "thumbnailPath", "sourceUrl"))
    return {value for value in values if value}


def is_supported_media(title: str, info: dict[str, Any]) -> bool:
    clean_title = title.partition(":")[2].casefold()
    mime = str(info.get("mime") or "").casefold()
    return clean_title.endswith(MEDIA_EXTENSIONS) and mime.startswith("image/")


def media_priority(title: str, location_name: str, width: int, height: int) -> str:
    filename = title.partition(":")[2]
    if LOW_VALUE_RE.search(filename):
        return "low"
    normalized_file = re.sub(r"[^a-z0-9]+", "", filename.casefold())
    normalized_location = re.sub(r"[^a-z0-9]+", "", location_name.casefold())
    if normalized_location and normalized_location in normalized_file and width >= 640 and height >= 360:
        return "high"
    if width >= 640 and height >= 360:
        return "medium"
    return "low"


def run_self_test() -> int:
    tests = {
        "pretty wiki URL": wiki_title("https://2b2t.wikioasis.org/wiki/Space_Valkyria") == "Space Valkyria",
        "query wiki URL": wiki_title("https://example.test/w/index.php?title=Old_Town") == "Old Town",
        "missing wiki URL": wiki_title(None) is None,
        "html metadata": flatten_metadata({"value": "<b>CC BY-SA</b>"}) == "CC BY-SA",
        "location filename priority": media_priority("File:Space_Valkyria.png", "Space Valkyria", 1200, 800) == "high",
        "logo low priority": media_priority("File:Space Valkyria Logo.png", "Space Valkyria", 1200, 800) == "low",
    }
    failed = [name for name, passed in tests.items() if not passed]
    print(json.dumps({"tests": len(tests), "failed": failed}))
    return 1 if failed else 0


def main() -> int:
    args = parse_args()
    if args.self_test:
        return run_self_test()

    locations = [location for location in load_locations(args) if wiki_title(location.get("wiki"))]
    if args.max_locations > 0:
        locations = locations[:args.max_locations]
    candidates: list[dict[str, Any]] = []
    errors: list[dict[str, Any]] = []
    pages_found = 0

    for index, location in enumerate(locations, 1):
        title = wiki_title(location.get("wiki"))
        assert title
        try:
            page_id, revision_id, image_titles = page_media(args.api, title)
            if page_id is None:
                errors.append({"locationId": location.get("rowid"), "locationName": location.get("name"),
                               "wikiTitle": title, "error": "wiki-page-not-found"})
                continue
            pages_found += 1
            infos = image_info(args.api, image_titles) if image_titles else {}
            known_urls = existing_urls(location)
            for image_title in image_titles:
                info = infos.get(image_title, {})
                if not is_supported_media(image_title, info):
                    continue
                metadata = info.get("extmetadata") or {}
                source_url = str(info.get("descriptionurl") or "")
                original_url = str(info.get("url") or "")
                thumbnail_url = str(info.get("thumburl") or "")
                width = int(info.get("width") or 0)
                height = int(info.get("height") or 0)
                linked = bool({source_url, original_url, thumbnail_url} & known_urls)
                candidates.append({
                    "locationId": location.get("rowid"),
                    "locationName": location.get("name"),
                    "wikiTitle": title,
                    "wikiUrl": location.get("wiki"),
                    "wikiPageId": page_id,
                    "wikiRevisionId": revision_id,
                    "fileTitle": image_title,
                    "mimeType": info.get("mime"),
                    "width": width,
                    "height": height,
                    "originalUrl": original_url,
                    "thumbnailUrl": thumbnail_url,
                    "sourceUrl": source_url,
                    "artist": flatten_metadata(metadata.get("Artist")),
                    "credit": flatten_metadata(metadata.get("Credit")),
                    "license": flatten_metadata(metadata.get("LicenseShortName")),
                    "usageTerms": flatten_metadata(metadata.get("UsageTerms")),
                    "attributionRequired": flatten_metadata(metadata.get("AttributionRequired")),
                    "reviewPriority": media_priority(image_title, str(location.get("name") or ""), width, height),
                    "alreadyLinked": linked,
                    "needsAttachment": not linked,
                    "evidence": "Image embedded on the location's existing Atlas-linked wiki page.",
                })
        except Exception as exc:
            errors.append({"locationId": location.get("rowid"), "locationName": location.get("name"),
                           "wikiTitle": title, "error": str(exc)})
        if index % 10 == 0 or index == len(locations):
            print(json.dumps({"progress": index, "total": len(locations),
                              "candidates": len(candidates), "errors": len(errors)}), flush=True)
        if args.delay_ms > 0 and index < len(locations):
            time.sleep(args.delay_ms / 1000)

    priority_order = {"high": 0, "medium": 1, "low": 2}
    candidates.sort(key=lambda item: (priority_order[item["reviewPriority"]],
                                      str(item["locationName"]).casefold(), str(item["fileTitle"]).casefold()))
    output = Path(args.output).resolve()
    report = {
        "schema": "atlas.wiki-location-media-candidates.v1",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "policy": "Review-only inventory. Direct reuse permission is recorded; verify location relevance and retain provenance before publishing.",
        "reuseAuthorization": REUSE_AUTHORIZATION,
        "locationsWithWikiScanned": len(locations),
        "wikiPagesFound": pages_found,
        "candidateCount": len(candidates),
        "unlinkedCandidateCount": sum(1 for item in candidates if item["needsAttachment"]),
        "errors": errors,
        "candidates": candidates,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    markdown = output.with_suffix(".md")
    lines = [
        "# 2b2t Wiki location-media candidates", "",
        f"Scanned {len(locations)} Atlas-linked wiki pages and found {len(candidates)} media candidates.", "",
        f"> {REUSE_AUTHORIZATION}", "",
        "> Review location relevance and retain source/creator attribution before publishing.", "",
    ]
    for item in candidates:
        status = "already linked" if item["alreadyLinked"] else "not linked"
        dimensions = f"{item['width']}×{item['height']}"
        lines.extend([
            f"## {item['locationName']} — {item['fileTitle'].partition(':')[2]}", "",
            f"- Priority: `{item['reviewPriority']}`; {status}; {dimensions}; `{item['mimeType']}`",
            f"- Source: [wiki file page]({item['sourceUrl']}) · [location article]({item['wikiUrl']})",
            f"- License: {item['license'] or item['usageTerms'] or 'not declared in API metadata'}",
            f"- Artist/credit: {item['artist'] or item['credit'] or 'not declared in API metadata'}", "",
        ])
    markdown.write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({
        "locations": len(locations), "pages": pages_found, "candidates": len(candidates),
        "errors": len(errors), "output": str(output),
    }))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
