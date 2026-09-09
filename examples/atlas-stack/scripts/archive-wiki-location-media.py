#!/usr/bin/env python3
"""Archive every unique media original from the Atlas 2b2t Wiki inventory.

Files are downloaded to fast D: staging, hashed, copied into a content-addressed
X: object store, verified a second time, and then removed from staging. The
archive records every wiki revision/location relationship even when several
pages reference the same object. It never publishes or attaches candidates.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import os
import shutil
import tempfile
import threading
import time
import urllib.parse
import urllib.request
import uuid
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_INVENTORY = r"C:\AtlasExample\Research\2b2t-wiki-media\media-candidates.json"
DEFAULT_ARCHIVE = r"E:\AtlasExample\HistoricalMedia\2b2t-wiki"
DEFAULT_STAGING = r"D:\AtlasExample\Ingest\media-staging\2b2t-wiki"
USER_AGENT = "2b2tAtlas preservation archive/1.0 (atlas.example)"
AUTHORIZATION = (
    "Direct reuse permission confirmed September 4, 2026 by Joey_Coconut, "
    "2b2t Wiki administrator and 2b2t Atlas team member."
)
EXTENSIONS = {"image/jpeg": ".jpg", "image/png": ".png", "image/webp": ".webp", "image/gif": ".gif"}
lock = threading.Lock()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", default=DEFAULT_INVENTORY)
    parser.add_argument("--archive-root", default=DEFAULT_ARCHIVE)
    parser.add_argument("--staging-root", default=DEFAULT_STAGING)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--max-download-mib", type=int, default=512)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(4 * 1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def assert_scoped(path: Path, root: Path) -> None:
    resolved = path.resolve()
    resolved_root = root.resolve()
    if resolved != resolved_root and resolved_root not in resolved.parents:
        raise ValueError(f"path escapes configured root: {resolved}")


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def download_to_staging(url: str, staging: Path, max_bytes: int) -> tuple[Path, str, int, str]:
    staging.mkdir(parents=True, exist_ok=True)
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    last_error: Exception | None = None
    for attempt in range(4):
        temporary: Path | None = None
        try:
            with urllib.request.urlopen(request, timeout=180) as response:
                mime = str(response.headers.get_content_type()).casefold()
                if mime not in EXTENSIONS:
                    raise ValueError(f"unsupported content type: {mime}")
                declared = response.headers.get("Content-Length")
                if declared and int(declared) > max_bytes:
                    raise ValueError(f"declared download exceeds {max_bytes} bytes")
                handle, name = tempfile.mkstemp(prefix="wiki-", suffix=".part", dir=staging)
                os.close(handle)
                temporary = Path(name)
                digest = hashlib.sha256()
                total = 0
                with temporary.open("wb") as output:
                    while True:
                        block = response.read(1024 * 1024)
                        if not block:
                            break
                        total += len(block)
                        if total > max_bytes:
                            raise ValueError(f"download exceeds {max_bytes} bytes")
                        digest.update(block)
                        output.write(block)
                if total == 0:
                    raise ValueError("empty response")
                return temporary, digest.hexdigest(), total, mime
        except Exception as exc:
            last_error = exc
            if temporary and temporary.exists():
                temporary.unlink()
            if attempt < 3:
                time.sleep(1.5 * (attempt + 1))
    assert last_error is not None
    raise last_error


def preserve_object(url: str, expected_mime: str, archive: Path, staging: Path,
                    max_bytes: int, known: dict[str, dict[str, Any]]) -> dict[str, Any]:
    existing = known.get(url)
    if existing:
        target = archive / str(existing["relativePath"])
        assert_scoped(target, archive)
        if target.exists() and target.stat().st_size == int(existing["bytes"]) and sha256_file(target) == existing["sha256"]:
            return existing

    temporary, digest, size, mime = download_to_staging(url, staging, max_bytes)
    try:
        if expected_mime and mime != expected_mime.casefold():
            raise ValueError(f"content type {mime} does not match inventory {expected_mime}")
        extension = EXTENSIONS[mime]
        relative = Path("objects") / digest[:2] / f"{digest}{extension}"
        target = archive / relative
        assert_scoped(target, archive)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            if target.stat().st_size != size or sha256_file(target) != digest:
                raise ValueError(f"existing archive object failed verification: {target}")
        else:
            incoming = target.with_name(target.name + f".incoming-{uuid.uuid4().hex}")
            assert_scoped(incoming, archive)
            shutil.copy2(temporary, incoming)
            if incoming.stat().st_size != size or sha256_file(incoming) != digest:
                incoming.unlink(missing_ok=True)
                raise ValueError("NAS copy failed SHA-256 verification")
            os.replace(incoming, target)
        return {
            "sourceUrl": url,
            "sha256": digest,
            "bytes": size,
            "mimeType": mime,
            "relativePath": relative.as_posix(),
            "archivedUtc": datetime.now(timezone.utc).isoformat(),
        }
    finally:
        temporary.unlink(missing_ok=True)


def write_jsonl_atomic(path: Path, rows: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".tmp-{uuid.uuid4().hex}")
    temporary.write_text("".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n" for row in rows), encoding="utf-8")
    os.replace(temporary, path)


def write_json_atomic(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".tmp-{uuid.uuid4().hex}")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def merge_objects(prior: list[dict[str, Any]], current: dict[str, dict[str, Any]]) -> list[dict[str, Any]]:
    by_url = {str(item.get("sourceUrl") or ""): item for item in prior if item.get("sourceUrl")}
    by_url.update(current)
    return sorted(by_url.values(), key=lambda item: (str(item.get("sha256") or ""), str(item.get("sourceUrl") or "")))


def canonical_wiki_url(value: Any) -> str:
    url = str(value or "")
    parsed = urllib.parse.urlparse(url)
    if parsed.hostname in {"2b2t.miraheze.org", "2b2t.wikioasis.org"}:
        return urllib.parse.urlunparse(("https", "2b2t.wikioasis.org", parsed.path, "", parsed.query, ""))
    return url


def prefer_reviewed(existing: dict[str, Any] | None, incoming: dict[str, Any]) -> dict[str, Any]:
    if existing is None:
        return incoming
    priority = {"low": 1, "medium": 2, "high": 3}
    existing_rank = priority.get(str(existing.get("reviewPriority") or "").casefold(), 0)
    incoming_rank = priority.get(str(incoming.get("reviewPriority") or "").casefold(), 0)
    return existing if existing_rank > incoming_rank else incoming


def relationship_key(item: dict[str, Any]) -> tuple[str, str, str]:
    return (
        str(item.get("locationId") or ""),
        str(item.get("fileTitle") or "").casefold(),
        canonical_wiki_url(item.get("sourceFilePage")).casefold(),
    )


def merge_relationships(prior: list[dict[str, Any]], current: list[dict[str, Any]]) -> list[dict[str, Any]]:
    merged: dict[tuple[str, str, str], dict[str, Any]] = {}
    for item in [*prior, *current]:
        key = relationship_key(item)
        merged[key] = prefer_reviewed(merged.get(key), item)
    return sorted(merged.values(), key=relationship_key)


def inventory_key(item: dict[str, Any]) -> tuple[str, str, str]:
    return (
        str(item.get("locationId") or ""),
        str(item.get("originalUrl") or "").casefold(),
        str(item.get("fileTitle") or "").casefold(),
    )


def merge_inventories(prior: dict[str, Any], current: dict[str, Any]) -> dict[str, Any]:
    merged: dict[tuple[str, str, str], dict[str, Any]] = {}
    for item in [*(prior.get("candidates") or []), *(current.get("candidates") or [])]:
        key = inventory_key(item)
        merged[key] = prefer_reviewed(merged.get(key), item)
    result = dict(current)
    result["schema"] = str(current.get("schema") or prior.get("schema") or "atlas.wiki-location-media.v1")
    result["generatedUtc"] = datetime.now(timezone.utc).isoformat()
    result["policy"] = "Merged preservation inventory; relationships are retained across bounded incremental archive runs."
    result["candidates"] = sorted(merged.values(), key=inventory_key)
    return result


def run_self_test() -> int:
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        child = root / "objects" / "a"
        assert_scoped(child, root)
        failed = False
        try:
            assert_scoped(root.parent / "outside", root)
        except ValueError:
            failed = True
        merged_objects = merge_objects(
            [{"sourceUrl": "old", "sha256": "a"}],
            {"new": {"sourceUrl": "new", "sha256": "b"}},
        )
        merged_relationships = merge_relationships(
            [{"locationId": 1, "fileTitle": "File:A", "sourceFilePage": "source", "value": "old"}],
            [{"locationId": 1, "fileTitle": "File:A", "sourceFilePage": "source", "value": "new"}],
        )
        migrated_relationships = merge_relationships(
            [{"locationId": 2, "fileTitle": "File:B", "sourceFilePage": "https://2b2t.wikioasis.org/wiki/File:B", "reviewPriority": "high"}],
            [{"locationId": 2, "fileTitle": "File:B", "sourceFilePage": "https://2b2t.miraheze.org/wiki/File:B", "reviewPriority": "medium"}],
        )
        merged_inventory = merge_inventories(
            {"candidates": [{"locationId": 1, "fileTitle": "File:A", "originalUrl": "a"}]},
            {"candidates": [{"locationId": 2, "fileTitle": "File:B", "originalUrl": "b"}]},
        )
        tests = {
            "scope accepts child": True,
            "scope rejects escape": failed,
            "authorization recorded": "Joey_Coconut" in AUTHORIZATION,
            "objects merge": len(merged_objects) == 2,
            "relationships update": len(merged_relationships) == 1 and merged_relationships[0]["value"] == "new",
            "wiki migration deduplicates": len(migrated_relationships) == 1,
            "higher review retained": migrated_relationships[0]["reviewPriority"] == "high",
            "inventories merge": len(merged_inventory["candidates"]) == 2,
        }
    bad = [name for name, passed in tests.items() if not passed]
    print(json.dumps({"tests": len(tests), "failed": bad}))
    return 1 if bad else 0


def main() -> int:
    args = parse_args()
    if args.self_test:
        return run_self_test()
    if args.workers < 1 or args.workers > 8:
        raise SystemExit("--workers must be between 1 and 8")
    inventory_path = Path(args.inventory).resolve()
    archive = Path(args.archive_root).resolve()
    staging = Path(args.staging_root).resolve()
    source = json.loads(inventory_path.read_text(encoding="utf-8"))
    candidates = list(source.get("candidates") or [])
    by_url: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for item in candidates:
        url = str(item.get("originalUrl") or "").strip()
        if url:
            by_url[url].append(item)
    print(json.dumps({"relationships": len(candidates), "uniqueObjects": len(by_url), "apply": args.apply,
                      "archiveRoot": str(archive), "stagingRoot": str(staging)}, indent=2))
    if not args.apply:
        return 0

    archive.mkdir(parents=True, exist_ok=True)
    staging.mkdir(parents=True, exist_ok=True)
    assert_scoped(archive / "objects", archive)
    assert_scoped(staging, staging)
    objects_path = archive / "objects.jsonl"
    prior_objects = read_jsonl(objects_path)
    prior_relationships = read_jsonl(archive / "relationships.jsonl")
    known = {str(item["sourceUrl"]): item for item in prior_objects}
    max_bytes = args.max_download_mib * 1024 * 1024
    results: dict[str, dict[str, Any]] = {}
    failures: list[dict[str, str]] = []

    def work(pair: tuple[str, list[dict[str, Any]]]) -> tuple[str, dict[str, Any]]:
        url, relations = pair
        expected_mime = str(relations[0].get("mimeType") or "").casefold()
        return url, preserve_object(url, expected_mime, archive, staging, max_bytes, known)

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(work, pair): pair[0] for pair in by_url.items()}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            url = futures[future]
            try:
                result_url, result = future.result()
                results[result_url] = result
            except Exception as exc:
                failures.append({"sourceUrl": url, "error": str(exc)})
            if index % 25 == 0 or index == len(futures):
                print(json.dumps({"progress": index, "total": len(futures), "archived": len(results), "failures": len(failures)}), flush=True)

    object_rows = merge_objects(prior_objects, results)
    current_relation_rows: list[dict[str, Any]] = []
    for item in candidates:
        archived = results.get(str(item.get("originalUrl") or ""))
        if archived is None:
            continue
        current_relation_rows.append({
            "locationId": item.get("locationId"),
            "locationName": item.get("locationName"),
            "wikiTitle": item.get("wikiTitle"),
            "wikiUrl": item.get("wikiUrl"),
            "wikiPageId": item.get("wikiPageId"),
            "wikiRevisionId": item.get("wikiRevisionId"),
            "fileTitle": item.get("fileTitle"),
            "sourceFilePage": item.get("sourceUrl"),
            "artist": item.get("artist"),
            "credit": item.get("credit"),
            "declaredLicense": item.get("license"),
            "usageTerms": item.get("usageTerms"),
            "reviewPriority": item.get("reviewPriority"),
            "relevanceEvidence": item.get("evidence"),
            "archiveSha256": archived.get("sha256"),
            "archiveRelativePath": archived.get("relativePath"),
        })
    relation_rows = merge_relationships(prior_relationships, current_relation_rows)
    write_jsonl_atomic(objects_path, object_rows)
    write_jsonl_atomic(archive / "relationships.jsonl", relation_rows)
    aggregate_inventory_path = archive / "source-inventory.json"
    prior_inventory = json.loads(aggregate_inventory_path.read_text(encoding="utf-8")) if aggregate_inventory_path.exists() else {}
    aggregate_inventory = merge_inventories(prior_inventory, source)
    write_json_atomic(aggregate_inventory_path, aggregate_inventory)
    metadata = {
        "schema": "atlas.preservation.wiki-media.v1",
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "authorization": AUTHORIZATION,
        "sourceInventory": str(aggregate_inventory_path),
        "sourceInventorySha256": sha256_file(aggregate_inventory_path),
        "latestInputInventory": str(inventory_path),
        "latestInputInventorySha256": sha256_file(inventory_path),
        "relationshipCount": len(relation_rows),
        "uniqueObjectCount": len(object_rows),
        "uniqueContentCount": len({item["sha256"] for item in object_rows}),
        "totalBytes": sum(int(item["bytes"]) for item in object_rows),
        "failures": failures,
    }
    write_json_atomic(archive / "archive.json", metadata)
    (archive / "README.txt").write_text(
        "2b2t Atlas preservation copy of media referenced by Atlas-linked 2b2t Wiki pages.\n"
        + AUTHORIZATION + "\n"
        "Objects are addressed by SHA-256. relationships.jsonl retains page, revision, source-file, creator, and review context.\n",
        encoding="utf-8",
    )
    print(json.dumps(metadata, ensure_ascii=False, indent=2))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
