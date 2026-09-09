#!/usr/bin/env python3
"""Self-host a bounded, high-confidence subset of reviewed 2b2t Wiki media.

The source inventory comes from audit-wiki-location-media.py. By default this
script only prints a plan. --apply downloads originals, creates WebP thumbnails,
and writes the generated Atlas seed manifest. Existing Atlas images count toward
the per-location ceiling, so reruns are deterministic and do not flood galleries.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import mimetypes
import re
import subprocess
import tempfile
import time
import urllib.parse
import urllib.request
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_CANDIDATES = r"C:\AtlasExample\Research\2b2t-wiki-media\media-candidates.json"
DEFAULT_LOCATIONS_URL = "http://127.0.0.1:5297/api/locations"
DEFAULT_DESTINATION = r"E:\2b2t\MiscRenders\LocationAttachments\wiki"
DEFAULT_PUBLIC_ROOT = "https://tiles.atlas.example/2b2t/MiscRenders/LocationAttachments/wiki"
DEFAULT_MANIFEST = "2b2tAtlas.Server/Data/wiki-location-media.json"
DEFAULT_REPORT = r"C:\AtlasExample\Research\2b2t-wiki-media\sync-report.json"
USER_AGENT = "2b2tAtlas reviewed wiki media sync/1.0 (atlas.example)"
PERMISSION = (
    "Direct reuse permission confirmed September 4, 2026 by Joey_Coconut, "
    "2b2t Wiki administrator and 2b2t Atlas team member."
)

# These exact files already exist in the hand-reviewed historical manifest.
MANUALLY_HOSTED_TITLES = {
    "File:Imperator's base.png",
    "File:Space Valkyria.png",
    "File:Summermelonbaserender.png",
    "File:EXIjh8O.png",
    "File:Rocket Town 1.png",
    "File:SkyCroppedWikiColor.jpg",
    "File:Smibville 2020-01-12a.png",
    "File:2k2k(2012-04-02 224532).png",
    "File:CampF.png",
}
PREFERRED_RE = re.compile(r"(?i)(?<![a-z])(?:render|overview|aerial|panorama|base|town|city)(?=[^a-z]|$)")
DEPRIORITIZED_RE = re.compile(r"(?i)(?<![a-z])(?:grief|destroyed|ruin|lavacast|map|heatmap)(?=[^a-z]|$)")
SAFE_STEM_RE = re.compile(r"[^a-z0-9]+")
MIME_EXTENSIONS = {
    "image/jpeg": ".jpg",
    "image/png": ".png",
    "image/webp": ".webp",
    "image/gif": ".gif",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidates", default=DEFAULT_CANDIDATES)
    parser.add_argument("--locations-url", default=DEFAULT_LOCATIONS_URL)
    parser.add_argument("--locations-json")
    parser.add_argument("--historical-manifest", default="2b2tAtlas.Server/Data/historical-location-media.json")
    parser.add_argument("--destination-root", default=DEFAULT_DESTINATION)
    parser.add_argument("--public-root", default=DEFAULT_PUBLIC_ROOT)
    parser.add_argument("--manifest-output", default=DEFAULT_MANIFEST)
    parser.add_argument("--report-output", default=DEFAULT_REPORT)
    parser.add_argument("--max-images-per-location", type=int, default=2)
    parser.add_argument("--max-download-mib", type=int, default=96)
    parser.add_argument("--magick", default="magick")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def slug(value: str, fallback: str = "media") -> str:
    result = SAFE_STEM_RE.sub("-", value.casefold()).strip("-")
    return result[:80] or fallback


def public_file_page(url: str) -> str:
    parsed = urllib.parse.urlparse(url)
    if parsed.path.startswith("/wiki/File:"):
        return urllib.parse.urlunparse(("https", "2b2t.wikioasis.org", parsed.path, "", parsed.query, ""))
    return url


def get_json(url: str) -> Any:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=45) as response:
        return json.load(response)


def load_locations(args: argparse.Namespace) -> list[dict[str, Any]]:
    if args.locations_json:
        return json.loads(Path(args.locations_json).read_text(encoding="utf-8"))
    return get_json(args.locations_url)


def load_existing_images(args: argparse.Namespace) -> tuple[dict[str, int], set[str]]:
    paths: dict[str, set[str]] = defaultdict(set)
    source_urls: set[str] = set()
    for location in load_locations(args):
        name = str(location.get("name") or "").casefold()
        for attachment in location.get("attachments") or []:
            if str(attachment.get("mediaType") or "").casefold() != "image":
                continue
            path = str(attachment.get("path") or "").strip()
            if path:
                paths[name].add(path.casefold())
            source = str(attachment.get("sourceUrl") or "").strip()
            if source:
                source_urls.add(public_file_page(source).casefold())
    manifest_paths = {Path(args.historical_manifest).resolve(), Path(args.manifest_output).resolve()}
    for manifest_path in manifest_paths:
        if not manifest_path.exists():
            continue
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if not isinstance(manifest, list):
            raise ValueError(f"media manifest must contain a JSON array: {manifest_path}")
        for item in manifest:
            if str(item.get("mediaType") or "").casefold() != "image":
                continue
            name = str(item.get("locationName") or "").casefold()
            path = str(item.get("path") or "").strip()
            if path:
                paths[name].add(path.casefold())
            source = str(item.get("sourceUrl") or "").strip()
            if source:
                source_urls.add(public_file_page(source).casefold())
    return {name: len(values) for name, values in paths.items()}, source_urls


def selection_score(item: dict[str, Any]) -> tuple[int, int, int, str]:
    title = str(item.get("fileTitle") or "")
    preferred = 1 if PREFERRED_RE.search(title) else 0
    deprioritized = 1 if DEPRIORITIZED_RE.search(title) else 0
    pixels = int(item.get("width") or 0) * int(item.get("height") or 0)
    return (-preferred, deprioritized, -pixels, title.casefold())


def ambiguous_location_names(items: list[dict[str, Any]]) -> set[str]:
    ids_by_name: dict[str, set[int]] = defaultdict(set)
    for item in items:
        ids_by_name[str(item.get("locationName") or "").casefold()].add(int(item["locationId"]))
    return {name for name, location_ids in ids_by_name.items() if len(location_ids) > 1}


def select_candidates(args: argparse.Namespace) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    source = json.loads(Path(args.candidates).read_text(encoding="utf-8"))
    existing_counts, existing_sources = load_existing_images(args)
    grouped: dict[int, list[dict[str, Any]]] = defaultdict(list)
    rejected = defaultdict(int)
    eligible: list[dict[str, Any]] = []
    for item in source.get("candidates") or []:
        if item.get("reviewPriority") != "high":
            rejected["not-high-confidence"] += 1
            continue
        if not item.get("needsAttachment", True):
            rejected["already-linked"] += 1
            continue
        if item.get("fileTitle") in MANUALLY_HOSTED_TITLES:
            rejected["manually-hosted"] += 1
            continue
        if public_file_page(str(item.get("sourceUrl") or "")).casefold() in existing_sources:
            rejected["existing-source"] += 1
            continue
        eligible.append(item)

    ambiguous_names = ambiguous_location_names(eligible)
    seen_sources: set[str] = set()
    for item in eligible:
        location_name = str(item.get("locationName") or "").casefold()
        if location_name in ambiguous_names:
            rejected["ambiguous-location-name"] += 1
            continue
        source_url = public_file_page(str(item.get("sourceUrl") or "")).casefold()
        if source_url in seen_sources:
            rejected["duplicate-source"] += 1
            continue
        seen_sources.add(source_url)
        grouped[int(item["locationId"])].append(item)

    selected: list[dict[str, Any]] = []
    for items in grouped.values():
        location_name = str(items[0].get("locationName") or "")
        available = max(0, args.max_images_per_location - existing_counts.get(location_name.casefold(), 0))
        if available == 0:
            rejected["location-at-ceiling"] += len(items)
            continue
        ranked = sorted(items, key=selection_score)
        selected.extend(ranked[:available])
        rejected["over-location-ceiling"] += max(0, len(ranked) - available)
    selected.sort(key=lambda item: (str(item["locationName"]).casefold(), selection_score(item)))
    return selected, {"sourceCandidates": len(source.get("candidates") or []), "rejected": dict(rejected)}


def download(url: str, destination: Path, max_bytes: int) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=120) as response:
        content_type = str(response.headers.get_content_type()).casefold()
        if content_type not in MIME_EXTENSIONS:
            raise ValueError(f"unsupported content type {content_type}")
        content_length = response.headers.get("Content-Length")
        if content_length and int(content_length) > max_bytes:
            raise ValueError(f"download exceeds {max_bytes} bytes")
        destination.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(dir=destination.parent, delete=False, suffix=".part") as temp:
            total = 0
            while True:
                block = response.read(1024 * 1024)
                if not block:
                    break
                total += len(block)
                if total > max_bytes:
                    temp.close()
                    Path(temp.name).unlink(missing_ok=True)
                    raise ValueError(f"download exceeds {max_bytes} bytes")
                temp.write(block)
            temp_path = Path(temp.name)
        temp_path.replace(destination)
        return content_type


def local_paths(item: dict[str, Any], root: Path) -> tuple[Path, Path, str]:
    location_slug = slug(str(item["locationName"]), f"location-{item['locationId']}")
    title = str(item["fileTitle"]).partition(":")[2]
    mime = str(item.get("mimeType") or "").casefold()
    extension = MIME_EXTENSIONS.get(mime) or Path(urllib.parse.urlparse(str(item["originalUrl"])).path).suffix.casefold()
    digest = hashlib.sha256(str(item["sourceUrl"]).encode("utf-8")).hexdigest()[:10]
    basename = f"{slug(Path(title).stem)}-{digest}"
    return root / location_slug / f"{basename}{extension}", root / location_slug / f"{basename}-thumb.webp", location_slug


def create_thumbnail(magick: str, original: Path, thumbnail: Path) -> None:
    thumbnail.parent.mkdir(parents=True, exist_ok=True)
    source = f"{original}[0]" if original.suffix.casefold() == ".gif" else str(original)
    completed = subprocess.run(
        [magick, source, "-auto-orient", "-thumbnail", "960x640>", "-strip", "-quality", "82", str(thumbnail)],
        capture_output=True, text=True, timeout=180, check=False,
    )
    if completed.returncode:
        raise RuntimeError(completed.stderr.strip() or f"ImageMagick exited {completed.returncode}")


def manifest_item(item: dict[str, Any], original: Path, thumbnail: Path, location_slug: str,
                  public_root: str) -> dict[str, Any]:
    title = str(item["fileTitle"]).partition(":")[2]
    public_base = public_root.rstrip("/") + "/" + urllib.parse.quote(location_slug)
    credit = item.get("artist") or item.get("credit")
    attribution = "2b2t Wiki contributors"
    if credit:
        attribution += f"; creator/credit recorded by the wiki: {credit}"
    attribution += f". {PERMISSION} See the source file page for revision history."
    return {
        "locationRowid": int(item["locationId"]),
        "locationName": item["locationName"],
        "fileName": Path(title).stem.replace("_", " "),
        "mediaType": "Image",
        "path": f"{public_base}/{urllib.parse.quote(original.name)}",
        "thumbnailPath": f"{public_base}/{urllib.parse.quote(thumbnail.name)}",
        "sourceUrl": public_file_page(str(item["sourceUrl"])),
        "caption": f"Historical image associated with {item['locationName']} on the 2b2t Wiki: {Path(title).stem.replace('_', ' ')}.",
        "attribution": attribution,
    }


def merge_manifest_entries(entries: list[dict[str, Any]]) -> list[dict[str, Any]]:
    deduplicated: dict[tuple[str, str], dict[str, Any]] = {}
    for item in entries:
        source = public_file_page(str(item.get("sourceUrl") or "")).casefold()
        path = str(item.get("path") or "").casefold()
        if not source and not path:
            raise ValueError("media manifest entry is missing both sourceUrl and path")
        key = ("source", source) if source else ("path", path)
        deduplicated[key] = item
    return sorted(
        deduplicated.values(),
        key=lambda item: (
            str(item.get("locationName") or "").casefold(),
            public_file_page(str(item.get("sourceUrl") or "")).casefold(),
            str(item.get("path") or "").casefold(),
        ),
    )


def run_self_test() -> int:
    merged = merge_manifest_entries([
        {"locationName": "Zed", "sourceUrl": "https://2b2t.miraheze.org/wiki/File:Same.png", "path": "old.png"},
        {"locationName": "Alpha", "sourceUrl": "https://2b2t.wikioasis.org/wiki/File:Same.png", "path": "new.png"},
        {"locationName": "Beta", "sourceUrl": "", "path": "unique.png"},
    ])
    tests = {
        "slug": slug("Imperator's Base") == "imperator-s-base",
        "public file page": public_file_page("https://2b2t.miraheze.org/wiki/File:Test.png") == "https://2b2t.wikioasis.org/wiki/File:Test.png",
        "preferred sort": selection_score({"fileTitle": "File:Town render.png", "width": 1, "height": 1}) < selection_score({"fileTitle": "File:Town grief.png", "width": 9, "height": 9}),
        "permission named": "Joey_Coconut" in PERMISSION,
        "merge preserves unique entries": len(merged) == 2,
        "merge canonicalizes wiki hosts": any(item["path"] == "new.png" for item in merged),
        "merge sorts deterministically": [item["locationName"] for item in merged] == ["Alpha", "Beta"],
        "ambiguous duplicate names rejected": ambiguous_location_names([
            {"locationId": 1, "locationName": "End Portal"},
            {"locationId": 2, "locationName": "end portal"},
            {"locationId": 3, "locationName": "Unique"},
        ]) == {"end portal"},
    }
    failed = [name for name, passed in tests.items() if not passed]
    print(json.dumps({"tests": len(tests), "failed": failed}))
    return 1 if failed else 0


def main() -> int:
    args = parse_args()
    if args.self_test:
        return run_self_test()
    if args.max_images_per_location < 1:
        raise SystemExit("--max-images-per-location must be positive")
    selected, summary = select_candidates(args)
    print(json.dumps({**summary, "selected": len(selected), "apply": args.apply}, indent=2))
    if not args.apply:
        for item in selected:
            print(f"{item['locationName']}\t{item['fileTitle']}\t{public_file_page(item['sourceUrl'])}")
        return 0

    destination_root = Path(args.destination_root)
    max_bytes = args.max_download_mib * 1024 * 1024
    output = Path(args.manifest_output)
    existing_manifest = json.loads(output.read_text(encoding="utf-8")) if output.exists() else []
    if not isinstance(existing_manifest, list):
        raise ValueError(f"media manifest must contain a JSON array: {output}")
    manifest: list[dict[str, Any]] = list(existing_manifest)
    published = 0
    failures: list[dict[str, str]] = []
    for index, item in enumerate(selected, 1):
        original, thumbnail, location_slug = local_paths(item, destination_root)
        try:
            if not original.exists():
                download(str(item["originalUrl"]), original, max_bytes)
            if not thumbnail.exists():
                create_thumbnail(args.magick, original, thumbnail)
            manifest.append(manifest_item(item, original, thumbnail, location_slug, args.public_root))
            published += 1
        except Exception as exc:
            failures.append({"locationName": str(item["locationName"]), "fileTitle": str(item["fileTitle"]), "error": str(exc)})
        if index % 10 == 0 or index == len(selected):
            print(json.dumps({"progress": index, "total": len(selected), "completed": published, "failures": len(failures)}), flush=True)
        time.sleep(0.05)

    manifest = merge_manifest_entries(manifest)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    report_output = Path(args.report_output)
    report_output.parent.mkdir(parents=True, exist_ok=True)
    report_output.write_text(json.dumps({
        "schema": "atlas.wiki-location-media-sync.v1",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "authorization": PERMISSION,
        "policy": f"High-confidence filename matches; no more than {args.max_images_per_location} Atlas images per location.",
        "selected": len(selected),
        "published": published,
        "preserved": len(existing_manifest),
        "manifestTotal": len(manifest),
        "failures": failures,
        **summary,
    }, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"manifest": str(output.resolve()), "published": published, "preserved": len(existing_manifest), "manifestTotal": len(manifest), "failures": len(failures)}))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
