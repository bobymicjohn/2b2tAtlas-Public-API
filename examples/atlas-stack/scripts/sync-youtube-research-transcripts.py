#!/usr/bin/env python3
"""Download and normalize timestamped YouTube transcripts for Atlas research.

Only captions and selected metadata are downloaded; video/audio streams are never
requested. Normalized records retain the video URL and millisecond timestamps so
facts proposed by later enrichment passes can be audited before publication.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import unicodedata
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

try:
    import yt_dlp
except ImportError as exc:
    raise SystemExit("Install yt-dlp first: python -m pip install --user yt-dlp") from exc


DEFAULT_TITLE_PATTERN = (
    r"(?i)2b2t|base|spawn|highway|road|canal|world border|faction|group|war|"
    r"city|town|castle|empire|imperial|rusher|veteran|mason|fuer|valkyria"
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--channel", required=True, help="Channel videos URL or a single video URL")
    parser.add_argument("--slug", required=True, help="Stable output folder name, for example fitmc")
    parser.add_argument(
        "--output-root",
        default=r"C:\AtlasExample\Research\2b2t-youtube",
        help="Research output root (default: C:\\AtlasExample\\Research\\2b2t-youtube)",
    )
    parser.add_argument("--title-pattern", default=DEFAULT_TITLE_PATTERN, help="Regex selecting channel videos")
    parser.add_argument("--max-videos", type=int, default=25, help="Maximum selected videos this run; 0 means all")
    parser.add_argument("--video-id", action="append", default=[], help="Process an exact video ID; repeatable")
    parser.add_argument("--refresh", action="store_true", help="Redownload already-normalized transcripts")
    parser.add_argument("--catalog-only", action="store_true", help="Discover videos without fetching captions")
    parser.add_argument("--caption-language", action="append", default=[],
                        help="Preferred caption language, repeatable (default: en-orig, en)")
    parser.add_argument("--max-consecutive-errors", type=int, default=3,
                        help="Stop when metadata is repeatedly unavailable; keep completed captions.")
    return parser.parse_args()


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def video_url(video_id: str) -> str:
    return f"https://www.youtube.com/watch?v={video_id}"


def get_catalog(source: str) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    options = {
        "extract_flat": "in_playlist",
        "ignoreerrors": True,
        "quiet": True,
        "no_warnings": True,
        "noprogress": True,
        "socket_timeout": 30,
        "skip_download": True,
    }
    with yt_dlp.YoutubeDL(options) as downloader:
        result = downloader.extract_info(source, download=False)
    if result is None:
        raise RuntimeError(f"YouTube returned no catalog for {source}")
    if result.get("_type") != "playlist":
        entry = {
            "id": result.get("id"),
            "title": result.get("title"),
            "url": result.get("webpage_url") or source,
            "duration": result.get("duration"),
        }
        return result, [entry]
    entries = [entry for entry in (result.get("entries") or []) if entry and entry.get("id")]
    return result, entries


def download_caption(video_id: str, raw_dir: Path, languages: list[str] | None = None) -> tuple[Path | None, Path | None]:
    languages = languages or ["en-orig", "en"]
    output = str(raw_dir / "%(id)s.%(ext)s")
    options = {
        "skip_download": True,
        "writesubtitles": True,
        "writeautomaticsub": True,
        "subtitleslangs": languages,
        "subtitlesformat": "json3",
        "writeinfojson": True,
        "outtmpl": output,
        "ignoreerrors": True,
        "quiet": True,
        "no_warnings": True,
        "noprogress": True,
        "socket_timeout": 30,
        "sleep_interval_requests": 1,
    }
    with yt_dlp.YoutubeDL(options) as downloader:
        downloader.download([video_url(video_id)])

    captions = [raw_dir / f"{video_id}.{language}.json3" for language in languages
                if (raw_dir / f"{video_id}.{language}.json3").exists()]
    info_path = raw_dir / f"{video_id}.info.json"
    return (captions[0] if captions else None), (info_path if info_path.exists() else None)


def normalize_caption(caption_path: Path, info_path: Path | None, fallback: dict[str, Any]) -> dict[str, Any]:
    caption = json.loads(caption_path.read_text(encoding="utf-8"))
    info = json.loads(info_path.read_text(encoding="utf-8")) if info_path else {}
    segments: list[dict[str, Any]] = []
    for event in caption.get("events", []):
        text = "".join(segment.get("utf8", "") for segment in event.get("segs", []))
        text = re.sub(r"\s+", " ", text).strip()
        if not text:
            continue
        start_ms = int(event.get("tStartMs", 0))
        duration_ms = int(event.get("dDurationMs", 0))
        segments.append({"startMs": start_ms, "durationMs": duration_ms, "text": text})

    video_id = str(info.get("id") or fallback["id"])
    return {
        "schema": "atlas.youtube-transcript.v1",
        "videoId": video_id,
        "url": info.get("webpage_url") or video_url(video_id),
        "channel": info.get("channel") or info.get("uploader"),
        "channelId": info.get("channel_id"),
        "title": info.get("title") or fallback.get("title"),
        "uploadDate": info.get("upload_date"),
        "durationSeconds": info.get("duration") or fallback.get("duration"),
        "captionLanguage": caption_path.name.removeprefix(video_id + ".").removesuffix(".json3"),
        "retrievedUtc": utc_now(),
        "segments": segments,
    }


def timestamp(milliseconds: int) -> str:
    seconds = milliseconds // 1000
    return f"{seconds // 3600:02d}:{(seconds % 3600) // 60:02d}:{seconds % 60:02d}"


def write_normalized(record: dict[str, Any], normalized_dir: Path) -> None:
    video_id = record["videoId"]
    temporary = normalized_dir / f"{video_id}.json.tmp"
    temporary.write_text(
        json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    temporary.replace(normalized_dir / f"{video_id}.json")
    lines = [
        f"# {record.get('title') or video_id}",
        f"URL: {record['url']}",
        f"Channel: {record.get('channel') or 'unknown'}",
        f"Upload date: {record.get('uploadDate') or 'unknown'}",
        "",
    ]
    lines.extend(f"[{timestamp(item['startMs'])}] {item['text']}" for item in record["segments"])
    (normalized_dir / f"{video_id}.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")


def select_caption_work(entries, normalized_dir, attempts, now_epoch, maximum):
    selected = [entry for entry in entries
                if not (normalized_dir / f"{entry['id']}.json").exists()
                and now_epoch - attempts.get(str(entry['id']), {}).get("epoch", 0) >= 7 * 86400]
    return selected[:maximum] if maximum > 0 else selected


def main() -> int:
    args = parse_args()
    channel_dir = Path(args.output_root).resolve() / args.slug
    raw_dir = channel_dir / "raw"
    normalized_dir = channel_dir / "normalized"
    raw_dir.mkdir(parents=True, exist_ok=True)
    normalized_dir.mkdir(parents=True, exist_ok=True)

    channel, entries = get_catalog(args.channel)
    catalog_path = channel_dir / "catalog.json"
    previous = json.loads(catalog_path.read_text(encoding="utf-8")) if catalog_path.exists() else {}
    old_channel_id = previous.get("channelId")
    channel_id = channel.get("channel_id") or channel.get("id")
    if old_channel_id and channel_id and old_channel_id != channel_id:
        raise ValueError("Output slug belongs to a different channel; use a separate slug")
    merged_entries = {str(entry["id"]): entry for entry in previous.get("entries", [])}
    merged_entries.update({str(entry["id"]): entry for entry in entries})
    catalog = {
        "schema": "atlas.youtube-catalog.v1",
        "source": args.channel,
        "channel": channel.get("channel") or channel.get("uploader"),
        "channelId": channel_id,
        "retrievedUtc": utc_now(),
        "sources": sorted(set(previous.get("sources", [previous.get("source")]) + [args.channel]) - {None}),
        "entries": list(merged_entries.values()),
    }
    (channel_dir / "catalog.json").write_text(
        json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    if args.catalog_only:
        print(json.dumps({"catalog": len(entries), "mergedCatalog": len(merged_entries), "output": str(channel_dir)}))
        return 0

    by_id = {str(entry["id"]): entry for entry in entries}
    if args.video_id:
        selected = [by_id.get(video_id, {"id": video_id, "title": None, "duration": None}) for video_id in args.video_id]
    else:
        selector = re.compile(args.title_pattern)
        selected = [entry for entry in entries if selector.search(unicodedata.normalize("NFKC", str(entry.get("title") or "")))]
    # Budget new work, not the first N catalog entries on every invocation.
    # Keep unsuccessful attempts on cooldown so captionless videos cannot starve
    # older uploads. Explicit IDs/--refresh are the operator's retry override.
    attempts_path = channel_dir / "caption-attempts.json"
    attempts = json.loads(attempts_path.read_text(encoding="utf-8")) if attempts_path.exists() else {}
    if not args.refresh and not args.video_id:
        now_epoch = datetime.now(timezone.utc).timestamp()
        selected = select_caption_work(selected, normalized_dir, attempts, now_epoch, args.max_videos)
    if args.max_videos > 0:
        selected = selected[: args.max_videos]

    completed: list[dict[str, Any]] = []
    consecutive_errors = 0
    for index, entry in enumerate(selected, 1):
        video_id = str(entry["id"])
        normalized_path = normalized_dir / f"{video_id}.json"
        if normalized_path.exists() and not args.refresh:
            completed.append({"videoId": video_id, "status": "existing", "title": entry.get("title")})
            continue
        print(f"[{index}/{len(selected)}] captions: {entry.get('title') or video_id}", flush=True)
        caption_path, info_path = download_caption(video_id, raw_dir, args.caption_language)
        attempts[video_id] = {"epoch": datetime.now(timezone.utc).timestamp(),
                              "status": "downloaded" if caption_path else "metadata-unavailable" if info_path is None else "no-requested-captions"}
        attempts_temp = attempts_path.with_suffix(".tmp")
        attempts_temp.write_text(json.dumps(attempts, indent=2) + "\n", encoding="utf-8")
        attempts_temp.replace(attempts_path)
        if info_path is None:
            consecutive_errors += 1
            completed.append({"videoId": video_id, "status": "metadata-unavailable", "title": entry.get("title")})
            if consecutive_errors >= max(1, args.max_consecutive_errors):
                print("Stopping after repeated metadata failures; completed captions are retained.", flush=True)
                break
            continue
        consecutive_errors = 0
        if caption_path is None:
            completed.append({"videoId": video_id, "status": "no-requested-captions", "title": entry.get("title")})
            continue
        record = normalize_caption(caption_path, info_path, entry)
        write_normalized(record, normalized_dir)
        completed.append({
            "videoId": video_id,
            "status": "downloaded",
            "title": record.get("title"),
            "segments": len(record["segments"]),
        })

    manifest = {
        "schema": "atlas.youtube-transcript-run.v1",
        "source": args.channel,
        "captionLanguages": args.caption_language or ["en-orig", "en"],
        "retrievedUtc": utc_now(),
        "catalogVideos": len(entries),
        "selectedVideos": len(selected),
        "processedVideos": len(completed),
        "stoppedEarly": len(completed) < len(selected),
        "results": completed,
    }
    (channel_dir / "last-run.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    history = channel_dir / "runs"
    history.mkdir(exist_ok=True)
    (history / (datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S-%f") + ".json")).write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"catalog": len(entries), "selected": len(selected), "output": str(channel_dir)}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
