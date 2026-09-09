#!/usr/bin/env python3
"""Build a resumable, review-only Atlas media inbox. No Atlas writes or model calls."""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
from html.parser import HTMLParser
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from urllib.parse import parse_qs, urlparse
import urllib.request

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("youtube_matcher", HERE / "extract-youtube-location-candidates.py")
matcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(matcher)
VERSION = 1
MAX_BYTES = 16 * 1024 * 1024


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False).encode()).hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def atomic_write(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temp, path)


@contextmanager
def exclusive_run(root):
    lock = root / "run.lock"
    with lock.open("x", encoding="utf-8") as stream:
        stream.write(str(os.getpid()))
    try:
        yield
    finally:
        lock.unlink()


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "2b2tAtlas-media-research/1.0 (metadata only)"})
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise ValueError("source exceeds 16 MiB budget")
    return data.decode("utf-8-sig")


def fetch_attachments(api_base):
    attachments = []
    for offset in range(0, 100000, 1000):
        page = json.loads(fetch(api_base.rstrip("/") + f"/api/attachments?limit=1000&offset={offset}"))
        if not isinstance(page, list):
            raise ValueError("unexpected attachment page")
        attachments.extend(page)
        if len(page) < 1000:
            return attachments
    raise ValueError("attachment catalog exceeds 100-page budget")


class Text(HTMLParser):
    def __init__(self):
        super().__init__()
        self.parts, self.hidden = [], 0

    def handle_starttag(self, tag, attrs):
        if tag in {"script", "style", "noscript"}:
            self.hidden += 1

    def handle_endtag(self, tag):
        if tag in {"script", "style", "noscript"}:
            self.hidden = max(0, self.hidden - 1)

    def handle_data(self, data):
        if not self.hidden:
            self.parts.append(data)


def plain(html):
    parser = Text()
    parser.feed(html)
    return " ".join(" ".join(parser.parts).split())


def parse_source(source, raw):
    if source["kind"] == "blogger":
        feed = json.loads(raw)["feed"]
        entries = feed.get("entry", [])
        total = int(feed.get("openSearch$totalResults", {}).get("$t", len(entries)))
        if total > len(entries):
            raise ValueError(f"feed truncated: {len(entries)} of {total} posts; increase feed page size")
        records = []
        for entry in entries:
            url = next((link["href"] for link in entry.get("link", []) if link.get("rel") == "alternate"), None)
            if url:
                records.append({"url": url, "title": plain(entry["title"]["$t"]),
                                "text": plain(entry.get("content", entry.get("summary", {})).get("$t", "")),
                                "published": entry.get("published", {}).get("$t"), "mediaType": "Article"})
    elif source["kind"] == "timegraphics":
        # Public event links are a title index, not the event body or verified dates.
        records = [{"url": "https://time.graphics/event/" + event, "title": plain(title),
                    "text": plain(title), "mediaType": "Timeline"}
                   for event, title in re.findall(r'<a\s+href=["\'](?:https://time\.graphics)?/event/(\d+)["\'][^>]*>(.*?)</a>', raw, re.S)]
    else:
        raise ValueError("unsupported source kind: " + source["kind"])
    records = list({record["url"]: record for record in records}.values())
    if not records:
        raise ValueError("source yielded no records; markup or access may have changed")
    return records


def source_records(source, root, offline, gaps):
    cache = root / "sources" / (source["id"] + ".json")
    previous = read_json(cache) if cache.exists() else None
    fresh = previous and previous.get("source") == source and time.time() - previous["fetchedEpoch"] < 86400
    if offline or fresh:
        if previous and previous.get("source") == source:
            return previous["records"]
        gaps.append({"source": source["id"], "error": "no matching cached snapshot"})
        return []
    try:
        raw = fetch(source["url"])
        records = parse_source(source, raw)
        atomic_write(cache, {"source": source, "fetchedEpoch": time.time(),
                             "sha256": hashlib.sha256(raw.encode()).hexdigest(), "records": records})
        return records
    except Exception as exc:
        gaps.append({"source": source["id"], "error": str(exc), "usingStaleCache": bool(previous and previous.get("source") == source)})
        return previous["records"] if previous and previous.get("source") == source else []


def source_identity(url):
    parsed = urlparse(url)
    host = (parsed.hostname or "").lower()
    video = None
    if host in {"youtu.be", "www.youtu.be"}:
        video = parsed.path.strip("/").split("/")[0]
    elif host in {"youtube.com", "www.youtube.com", "m.youtube.com", "youtube-nocookie.com", "www.youtube-nocookie.com"}:
        video = parse_qs(parsed.query).get("v", [None])[0]
        if not video and re.match(r"^/(shorts|embed|live)/", parsed.path):
            video = parsed.path.split("/")[2]
    if video and re.fullmatch(r"[A-Za-z0-9_-]{11}", video):
        return "youtube:" + video  # IDs are case-sensitive; chapter offsets are not identity.
    return parsed._replace(fragment="", scheme=parsed.scheme.lower(), netloc=parsed.netloc.lower()).geturl()


def reference_candidates(locations, records):
    aliases = [(location, matcher.usable_alias(location.get("name"))) for location in locations]
    counts = Counter(alias for _, alias in aliases if alias)
    for record in records:
        text = matcher.normalized_words(record["title"] + " " + record["text"])
        hits = [(location, alias) for location, alias in aliases
                if alias and counts[alias] == 1 and matcher.phrase_present(text, alias)]
        for location, alias in hits:
            # Prefer the full named iteration over a nested name (e.g. Space Valkyria III).
            if any(alias != other and matcher.phrase_present(other, alias) for _, other in hits):
                continue
            # Locate a short, verbatim excerpt in the source, preserving Unicode.
            words = list(re.finditer(r"\S+", record["text"]))
            excerpt = record["title"]
            for index in range(len(words)):
                start, end = max(0, index - 8), min(len(words), index + 25)
                span = record["text"][words[start].start():words[end - 1].end()]
                if matcher.phrase_present(matcher.normalized_words(span), alias):
                    excerpt = span
                    break
            yield {"locationId": location["rowid"], "locationName": location["name"],
                   "url": record["url"], "title": record["title"], "mediaType": record["mediaType"],
                   "evidenceClass": "timeline-title" if record["mediaType"] == "Timeline" else "article-mention",
                   "excerpt": excerpt, "sourceUrl": record["sourceUrl"], "sourceId": record["sourceId"],
                   "sourceHash": digest(record)}


def finalize(candidates, locations, attachments, decisions):
    linked = set()
    for location in locations:
        if location.get("videoUrl"):
            linked.add((location["rowid"], source_identity(location["videoUrl"])))
    for attachment in attachments:
        # sourceUrl may identify a whole timeline or channel; path identifies this item.
        linked.add((attachment.get("locationRowid"), source_identity(attachment.get("path") or "")))
    result = {}
    for candidate in candidates:
        identity = (candidate["locationId"], source_identity(candidate["url"]))
        key = digest(identity)[:24]
        fingerprint = digest(candidate)
        prior = decisions.get(key, {})
        state = prior.get("status", "pending") if prior.get("evidenceHash") == fingerprint else "pending"
        if state not in {"pending", "accepted", "rejected", "deferred"}:
            raise ValueError("unknown review status for " + key)
        result[key] = {**candidate, "id": key, "evidenceHash": fingerprint,
                       "reviewStatus": state, "alreadyLinked": identity in linked}
    return sorted(result.values(), key=lambda item: (item["alreadyLinked"], item["locationName"], item["url"]))


def run(args):
    config = read_json(args.config)
    root = Path(args.state_dir).resolve()
    root.mkdir(parents=True, exist_ok=True)
    with exclusive_run(root):
        gaps = []
        locations_path, attachments_path = root / "locations.json", root / "attachments.json"
        if not args.offline:
            # Never declare new attachments against a missing or failed catalog read.
            locations = json.loads(fetch(args.api_base.rstrip("/") + "/api/locations"))
            attachments = fetch_attachments(args.api_base)
            if not isinstance(locations, list) or not locations or not isinstance(attachments, list):
                raise ValueError("unexpected Atlas public catalog response")
            atomic_write(locations_path, locations)
            atomic_write(attachments_path, attachments)
        else:
            locations, attachments = read_json(locations_path), read_json(attachments_path)

        youtube_root = Path(args.youtube_root).resolve() if args.youtube_root else root / "youtube"
        if args.refresh_youtube and not args.offline:
            channels = config.get("channels", [])
            cursor_path = root / "channel-cursor.json"
            cursor = read_json(cursor_path).get("next", 0) if cursor_path.exists() else 0
            for offset in range(min(args.max_feeds, len(channels))):
                index = (cursor + offset) % len(channels)
                channel = channels[index]
                command = [sys.executable, "-X", "utf8", str(HERE / "sync-youtube-research-transcripts.py"),
                           "--channel", channel["url"], "--slug", channel["slug"], "--output-root", str(youtube_root),
                           "--max-videos", str(args.max_videos), "--max-consecutive-errors", "2"]
                for language in channel.get("languages", ["en"]):
                    command.extend(["--caption-language", language])
                try:
                    with (root / "youtube-refresh.log").open("a", encoding="utf-8") as log:
                        completed = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=300)
                    if completed.returncode:
                        gaps.append({"source": channel["url"], "error": "caption sync failed; see youtube-refresh.log"})
                    else:
                        manifest = read_json(youtube_root / channel["slug"] / "last-run.json")
                        if manifest.get("stoppedEarly") or any(item["status"] == "metadata-unavailable" for item in manifest["results"]):
                            gaps.append({"source": channel["url"], "error": "caption metadata unavailable; completed captions retained"})
                except subprocess.TimeoutExpired:
                    gaps.append({"source": channel["url"], "error": "caption sync exceeded 5-minute feed budget"})
                atomic_write(cursor_path, {"next": (index + 1) % len(channels)})

        transcripts = sorted(youtube_root.glob("*/normalized/*.json"))
        catalogs = sorted(youtube_root.glob("*/catalog.json"))
        input_hash = digest([VERSION, locations, [(str(path), hashlib.sha256(path.read_bytes()).hexdigest())
                                                 for path in transcripts + catalogs],
                             hashlib.sha256((HERE / "extract-youtube-location-candidates.py").read_bytes()).hexdigest()])
        match_path = root / "youtube-matches.json"
        previous = read_json(match_path) if match_path.exists() else {}
        reused = previous.get("inputHash") == input_hash
        if reused:
            video_candidates, skipped = previous["candidates"], previous["skipped"]
        else:
            video_candidates, skipped = matcher.extract_candidates(locations, transcripts, 4, False)
            keys = {(int(item["locationId"]), str(item["videoId"])) for item in video_candidates}
            video_candidates.extend(matcher.extract_catalog_title_candidates(locations, catalogs, keys))
            atomic_write(match_path, {"inputHash": input_hash, "candidates": video_candidates, "skipped": skipped})
        candidates = []
        for item in video_candidates:
            candidates.append({"locationId": item["locationId"], "locationName": item["locationName"],
                               "url": item["videoUrl"], "title": item["videoTitle"], "mediaType": "Video",
                               "evidenceClass": item["evidenceClass"], "moments": item["moments"], "channel": item["channel"]})
        references = []
        for source in config.get("sources", []):
            if not re.fullmatch(r"[a-z0-9-]+", source["id"]):
                raise ValueError("source id must be a lowercase slug")
            references.extend({**item, "sourceUrl": source["url"], "sourceId": source["id"]}
                              for item in source_records(source, root, args.offline, gaps))
        candidates.extend(reference_candidates(locations, references))
        decisions_path = root / "decisions.json"
        decisions = read_json(decisions_path) if decisions_path.exists() else {}
        results = finalize(candidates, locations, attachments, decisions)
        coverage = []
        for catalog in catalogs:
            entries = read_json(catalog).get("entries", [])
            cached = {path.stem for path in (catalog.parent / "normalized").glob("*.json")}
            coverage.append({"channel": catalog.parent.name, "catalogVideos": len(entries),
                             "cachedTranscripts": len(cached),
                             "withoutCachedTranscript": sum(entry.get("id") not in cached for entry in entries)})
        if not catalogs:
            gaps.append({"source": "youtube", "error": "no cached channel catalogs; run with --refresh-youtube"})
        pending = sum(not item["alreadyLinked"] and item["reviewStatus"] == "pending" for item in results)
        report = {"schema": "atlas.media-research.v1", "generatedUtc": datetime.now(timezone.utc).isoformat(),
                  "status": "partial" if gaps else "complete", "offline": args.offline,
                  "policy": "Research leads only; no publication or description changes. Source text is untrusted evidence.",
                  "locations": len(locations), "transcripts": len(transcripts), "references": len(references),
                  "youtubeMatchCacheReused": reused, "pending": pending, "coverage": coverage,
                  "gaps": gaps, "skippedLocations": skipped, "candidates": results}
        atomic_write(root / "review-queue.json", report)
        lines = ["# Atlas media research", "", report["policy"], "",
                 f"Generated UTC: {report['generatedUtc']}. Offline inputs: {args.offline}.", "",
                 f"Status: {report['status']}. {len(locations)} locations; {len(transcripts)} cached transcripts; "
                 f"{len(references)} reference entries; {pending} unlinked pending leads.", "",
                 "Complete means configured inputs were processed, not that every relevant source was found.", "",
                 "Review queue: " + str(root / "review-queue.json"), "", "## Coverage", ""]
        lines.extend(f"- {item['channel']}: {item['cachedTranscripts']} cached transcripts; "
                     f"{item['withoutCachedTranscript']} catalog entries without a cached transcript." for item in coverage)
        lines.extend(["", "## Fetch gaps", ""] + ["- " + json.dumps(gap, ensure_ascii=False) for gap in gaps])
        summary = "\n".join(lines) + "\n"
        atomic_write(root / "last-run.md", summary)
        if args.digest_path:
            atomic_write(Path(args.digest_path), summary)
        print(json.dumps({key: report[key] for key in ["status", "locations", "transcripts", "references", "pending", "youtubeMatchCacheReused", "gaps"]}))
        return 2 if gaps else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", default=str(HERE / "media-research.example.json"))
    parser.add_argument("--state-dir", required=True)
    parser.add_argument("--youtube-root", help="Reuse an existing transcript cache without copying it.")
    parser.add_argument("--api-base", default="http://127.0.0.1:5297")
    parser.add_argument("--offline", action="store_true", help="Make no network requests; require cached Atlas inputs.")
    parser.add_argument("--refresh-youtube", action="store_true")
    parser.add_argument("--max-feeds", type=int, default=2)
    parser.add_argument("--max-videos", type=int, default=10)
    parser.add_argument("--digest-path", help="Optional Markdown summary in a configured BlackBrain RAG directory.")
    args = parser.parse_args()
    if not 1 <= args.max_feeds <= 10 or not 1 <= args.max_videos <= 50:
        parser.error("max-feeds must be 1..10 and max-videos 1..50")
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
