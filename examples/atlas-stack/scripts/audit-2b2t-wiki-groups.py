#!/usr/bin/env python3
"""Create a revision-pinned, review-only audit of 2b2t Wiki group articles.

The public WikiOasis site currently protects its API with a browser challenge, while
the wiki's MediaWiki backend remains available at the former Miraheze API hostname.
This collector reads that API and rewrites review links to the public WikiOasis host.
It never writes to an Atlas database.
"""

from __future__ import annotations

import argparse
import difflib
import html
import json
import re
import sqlite3
import time
import urllib.parse
import urllib.request
from collections import defaultdict
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


DEFAULT_API = "https://2b2t.miraheze.org/w/api.php"
DEFAULT_PUBLIC_WIKI = "https://2b2t.wikioasis.org/wiki/"
USER_AGENT = "2b2tAtlas wiki group audit/2.0 (review-only; contact via atlas.example)"
GROUP_CATEGORIES = {
    "Base groups",
    "Building groups",
    "Conglomerative groups",
    "Disbanded groups",
    "Event groups",
    "Exclusive groups",
    "Exploiting groups",
    "Griefing groups",
    "Groups",
    "Highway digging groups",
    "PvP groups",
    "Rusher groups",
}
NON_ARTICLES = {"Front Page"}
IGNORED_LINK_PREFIXES = (
    "category:", "file:", "image:", "template:", "user:", "help:",
    "special:", "2b2t wiki:", "quarantine:",
)

# Explicit, human-reviewed wiki aliases for Atlas group identities. These are
# deliberately not inferred from fuzzy similarity or article prose: a bad group
# merge would contaminate every downstream build attribution. Each entry must be
# backed by a revision-pinned article that names both identities as the same group.
REVIEWED_GROUP_ALIASES = {
    "The Society": "The Society Project",
}


@dataclass
class AtlasGroup:
    id: int
    name: str
    type: str
    description: str | None
    wiki_url: str | None


def api_get(api: str, params: dict[str, str], attempts: int = 4) -> dict[str, Any]:
    query = urllib.parse.urlencode({"format": "json", "formatversion": "2", **params})
    request = urllib.request.Request(f"{api}?{query}", headers={"User-Agent": USER_AGENT})
    for attempt in range(attempts):
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                return json.load(response)
        except Exception:
            if attempt + 1 == attempts:
                raise
            time.sleep(1.5 * (attempt + 1))
    raise RuntimeError("unreachable")


def continued_query(api: str, params: dict[str, str], result_key: str) -> list[dict[str, Any]]:
    results: list[dict[str, Any]] = []
    continuation: dict[str, str] = {}
    while True:
        payload = api_get(api, {**params, **continuation})
        results.extend(payload.get("query", {}).get(result_key, []))
        continuation = payload.get("continue", {})
        if not continuation:
            return results


def normalize(value: str | None) -> str:
    if not value:
        return ""
    value = html.unescape(value).casefold().replace("&", " and ")
    return re.sub(r"[^a-z0-9]+", "", value)


ROMAN_SUFFIXES = {
    "i": "1", "ii": "2", "iii": "3", "iv": "4", "v": "5",
    "vi": "6", "vii": "7", "viii": "8", "ix": "9", "x": "10",
}


def normalize_iteration(value: str | None) -> str:
    """Normalize only an explicit trailing Roman iteration; never drop an iteration."""
    if not value:
        return ""
    tokens = re.sub(r"[^a-z0-9]+", " ", value.casefold()).strip().split()
    if tokens and tokens[-1] in ROMAN_SUFFIXES:
        tokens[-1] = ROMAN_SUFFIXES[tokens[-1]]
    return "".join(tokens)


def clean_wikitext(value: str | None) -> str | None:
    if not value:
        return None
    value = re.sub(r"<!--.*?-->", "", value, flags=re.S)
    value = re.sub(r"<ref\b[^>]*>.*?</ref>|<ref\b[^>]*/>", "", value, flags=re.I | re.S)
    value = re.sub(r"\[\[([^]|]+)\|([^]]+)]]", r"\2", value)
    value = re.sub(r"\[\[([^]]+)]]", r"\1", value)
    value = re.sub(r"\[(https?://\S+)\s+([^]]+)]", r"\2", value)
    value = re.sub(r"\{\{[^{}]*}}", "", value)
    value = value.replace("'''", "").replace("''", "")
    value = re.sub(r"<[^>]+>", "", value)
    value = re.sub(r"\s+", " ", html.unescape(value)).strip(" |,;")
    return value or None


def extract_balanced_template(wikitext: str, template_name: str) -> str | None:
    match = re.search(r"\{\{\s*" + re.escape(template_name) + r"\b", wikitext, re.I)
    if not match:
        return None
    depth = 0
    index = match.start()
    while index < len(wikitext) - 1:
        pair = wikitext[index:index + 2]
        if pair == "{{":
            depth += 1
            index += 2
            continue
        if pair == "}}":
            depth -= 1
            index += 2
            if depth == 0:
                return wikitext[match.start():index]
            continue
        index += 1
    return None


def parse_infobox(wikitext: str) -> dict[str, str]:
    template = extract_balanced_template(wikitext, "Infobox group")
    if not template:
        return {}
    fields: dict[str, str] = {}
    current: str | None = None
    for line in template.splitlines()[1:]:
        field = re.match(r"^\s*\|\s*([^=]+?)\s*=\s*(.*)$", line)
        if field:
            current = re.sub(r"\s+", "_", field.group(1).strip().casefold())
            fields[current] = field.group(2).strip()
        elif current and line.strip() and not line.lstrip().startswith("}}"):
            fields[current] += " " + line.strip()
    return {key: value for key, value in fields.items() if value.strip()}


def first_field(fields: dict[str, str], *names: str) -> str | None:
    for name in names:
        value = clean_wikitext(fields.get(name))
        if value:
            return value
    return None


def infobox_image_name(fields: dict[str, str], *names: str) -> str | None:
    """Preserve a File: target instead of mistaking a wiki-link display option such as 'thumb' for its name."""
    for name in names:
        raw = fields.get(name)
        if not raw:
            continue
        linked = re.search(r"\[\[\s*(?:File|Image)\s*:\s*([^|\]]+)", raw, re.I)
        if linked:
            return "File:" + html.unescape(linked.group(1)).strip()
        plain = clean_wikitext(raw)
        if plain and re.search(r"\.(?:png|jpe?g|gif|webp|svg)$", plain, re.I):
            return plain
    return None


def classify(categories: set[str]) -> str:
    build = bool(categories & {"Base groups", "Building groups"})
    highway = "Highway digging groups" in categories
    if build and highway:
        return "Mixed"
    if build:
        return "Build"
    if highway:
        return "Highway"
    return "Other"


def wiki_links(wikitext: str) -> list[str]:
    links: set[str] = set()
    for target in re.findall(r"\[\[([^]|#]+)(?:#[^]|]*)?(?:\|[^]]*)?]]", wikitext):
        candidate = html.unescape(target).strip()
        if candidate and not candidate.casefold().startswith(IGNORED_LINK_PREFIXES):
            links.add(candidate)
    return sorted(links, key=str.casefold)


EXCLUDED_EVIDENCE_HEADING = re.compile(
    r"(?i)\b(?:builders?|members?|residents?|leadership|history|gallery|references?|"
    r"see also|former|post[- ]|involving|inspired|comparison|griefs?|conflicts?|allies)\b"
)
GENERIC_BUILD_HEADING = re.compile(
    r"(?i)^(?:the )?(?:bases?|builds?|projects?|outposts?|lodges?|notable (?:bases?|builds?))$"
)


def _heading_candidate(label: str) -> str | None:
    """Return a concrete project name from a heading, never an evidence-role heading."""
    candidate = (clean_wikitext(label) or "").rstrip(":").strip()
    if not candidate or EXCLUDED_EVIDENCE_HEADING.search(candidate) or GENERIC_BUILD_HEADING.fullmatch(candidate):
        return None
    candidate = re.sub(r"(?i)\s+(?:first|second|third)?\s*rebuild$", "", candidate).strip()
    return candidate if candidate and len(candidate) <= 100 else None


def _list_candidate(line: str) -> str | None:
    """Extract the declared subject of a list row without harvesting links from its explanation."""
    match = re.match(r"^\s*[*#-]\s*(.+)$", line)
    if not match:
        return None
    subject = match.group(1).strip()
    linked = re.match(r"\[\[([^]|#]+)(?:#[^]|]*)?(?:\|[^]]*)?]]", subject)
    if linked:
        return html.unescape(linked.group(1)).strip()
    underlined = re.match(r"(?i)<u>\s*(.*?)\s*</u>", subject)
    if underlined:
        return clean_wikitext(underlined.group(1))
    subject = re.split(r"\s+(?:-|â€“|â€”|–|—)\s+|\s*\(", subject, maxsplit=1)[0]
    subject = clean_wikitext(subject)
    return subject.rstrip(":").strip() if subject and len(subject) <= 100 else None


def section_candidates(wikitext: str) -> list[str]:
    """Find explicitly declared projects in base/build sections.

    Arbitrary links in prose are deliberately excluded: comparisons, memorials, builders,
    visitors, and former-member work are context, not group ownership evidence.
    """
    headings = list(re.finditer(r"(?m)^(={2,6})\s*(.*?)\s*\1\s*$", wikitext))
    candidates: set[str] = set()
    for index, heading in enumerate(headings):
        level = len(heading.group(1))
        label = clean_wikitext(heading.group(2)) or ""
        if not re.search(r"(?i)\b(base|bases|build|builds|projects?|outposts?|lodges?)\b", label):
            continue
        if EXCLUDED_EVIDENCE_HEADING.search(label):
            continue
        end = len(wikitext)
        for following in headings[index + 1:]:
            if len(following.group(1)) <= level:
                end = following.start()
                break
        own_candidate = _heading_candidate(label)
        if own_candidate:
            candidates.add(own_candidate)

        body = wikitext[heading.end():end]
        excluded_level: int | None = None
        for line in body.splitlines():
            child_heading = re.match(r"^(={2,6})\s*(.*?)\s*\1\s*$", line)
            if child_heading:
                child_level = len(child_heading.group(1))
                child_label = clean_wikitext(child_heading.group(2)) or ""
                if excluded_level is not None and child_level <= excluded_level:
                    excluded_level = None
                if EXCLUDED_EVIDENCE_HEADING.search(child_label):
                    excluded_level = child_level
                continue
            if excluded_level is not None:
                continue
            main_article = re.search(r"(?i)\bmain\s+article\s*:\s*\[\[([^]|#]+)", line)
            if main_article:
                candidates.add(html.unescape(main_article.group(1)).strip())
                continue
            listed = _list_candidate(line)
            if listed:
                candidates.add(listed)
        for child in headings[index + 1:]:
            if child.start() >= end:
                break
            child_label = _heading_candidate(child.group(2))
            if child_label:
                candidates.add(child_label)
    return sorted(candidates, key=str.casefold)


def infobox_base_candidates(raw_value: str | None) -> list[str]:
    if not raw_value:
        return []
    value = re.sub(r"(?i)<br\s*/?>", ",", raw_value)
    value = clean_wikitext(value) or ""
    candidates: set[str] = set()
    for part in re.split(r"[,;]", value):
        part = re.sub(r"\s*\([^)]*\)\s*$", "", part).strip()
        part = re.sub(r"(?i)\s+and others?$", "", part).strip()
        if part and len(part) <= 100 and not re.fullmatch(r"[~+\d\s–—-]+", part):
            candidates.add(part)
    return sorted(candidates, key=str.casefold)


def external_urls(wikitext: str) -> list[str]:
    return sorted(set(re.findall(r"https?://[^\s\]|<>}]+", wikitext)), key=str.casefold)


def load_atlas(db_path: Path) -> tuple[list[AtlasGroup], list[dict[str, Any]], set[tuple[int, int]]]:
    uri = f"file:{db_path.as_posix()}?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    connection.row_factory = sqlite3.Row
    try:
        group_columns = {row[1] for row in connection.execute("pragma table_info(Groups)")}
        select = ["Id", "Name", "Type", "Description", "WikiUrl"]
        existing = [column for column in select if column in group_columns]
        groups = [AtlasGroup(
            id=int(row["Id"]), name=row["Name"], type=row["Type"],
            description=row["Description"] if "Description" in row.keys() else None,
            wiki_url=row["WikiUrl"] if "WikiUrl" in row.keys() else None,
        ) for row in connection.execute(f"select {','.join(existing)} from Groups")]
        locations = [dict(row) for row in connection.execute(
            "select Rowid,Name,Dimension,X,Z from Locations order by Rowid")]
        tables = {row[0] for row in connection.execute(
            "select name from sqlite_master where type='table'")}
        reviewed_links = set()
        if "LocationGroups" in tables:
            reviewed_links = {
                (int(row[0]), int(row[1]))
                for row in connection.execute("select LocationRowid,GroupId from LocationGroups")
            }
        return groups, locations, reviewed_links
    finally:
        connection.close()


def public_url(base: str, title: str) -> str:
    return base.rstrip("/") + "/" + urllib.parse.quote(title.replace(" ", "_"), safe="()'!$,-._~")


def wiki_title_from_url(value: str | None) -> str | None:
    if not value:
        return None
    parsed = urllib.parse.urlparse(value)
    marker = "/wiki/"
    if marker not in parsed.path:
        return None
    return urllib.parse.unquote(parsed.path.split(marker, 1)[1]).replace("_", " ")


def fetch_articles(api: str) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    category_members: list[dict[str, Any]] = []
    for category in sorted(GROUP_CATEGORIES):
        category_members.extend(continued_query(api, {
            "action": "query", "list": "categorymembers", "cmtitle": f"Category:{category}",
            "cmnamespace": "0", "cmtype": "page", "cmlimit": "max",
        }, "categorymembers"))
    titles = sorted({item["title"] for item in category_members} - NON_ARTICLES, key=str.casefold)
    articles: list[dict[str, Any]] = []
    for offset in range(0, len(titles), 10):
        batch = titles[offset:offset + 10]
        payload = api_get(api, {
            "action": "query", "prop": "revisions|extracts|info|pageimages|categories",
            "rvprop": "ids|timestamp|content", "rvslots": "main", "exintro": "1",
            "explaintext": "1", "inprop": "url", "piprop": "original|thumbnail",
            "pithumbsize": "512", "cllimit": "max", "titles": "|".join(batch),
        })
        articles.extend(payload.get("query", {}).get("pages", []))
        time.sleep(0.08)
    site = api_get(api, {"action": "query", "meta": "siteinfo", "siprop": "general"})
    return articles, site.get("query", {}).get("general", {})


def make_report(
    articles: list[dict[str, Any]], atlas_groups: list[AtlasGroup],
    locations: list[dict[str, Any]], reviewed_links: set[tuple[int, int]],
    api: str, public_base: str, site: dict[str, Any],
) -> dict[str, Any]:
    location_index: dict[str, list[dict[str, Any]]] = defaultdict(list)
    iteration_location_index: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for location in locations:
        location_index[normalize(location["Name"])].append(location)
        iteration_location_index[normalize_iteration(location["Name"])].append(location)
    atlas_group_index: dict[str, list[AtlasGroup]] = defaultdict(list)
    atlas_group_wiki_index: dict[str, list[AtlasGroup]] = defaultdict(list)
    group_identity_keys: set[str] = set()
    for group in atlas_groups:
        group_key = normalize(group.name)
        atlas_group_index[group_key].append(group)
        group_identity_keys.add(group_key)
        wiki_title = wiki_title_from_url(group.wiki_url)
        if wiki_title:
            wiki_key = normalize(wiki_title)
            atlas_group_wiki_index[wiki_key].append(group)
            group_identity_keys.add(wiki_key)
    for alias, target in REVIEWED_GROUP_ALIASES.items():
        group_identity_keys.update({normalize(alias), normalize(target)})

    records: list[dict[str, Any]] = []
    for page in articles:
        revisions = page.get("revisions", [])
        revision = revisions[0] if revisions else {}
        content = revision.get("slots", {}).get("main", {}).get("content", "")
        redirect = re.search(r"(?im)^#redirect\s*\[\[([^]]+)]]", content)
        if redirect:
            continue
        infobox = parse_infobox(content)
        categories = {item["title"].removeprefix("Category:") for item in page.get("categories", [])}
        if not infobox and not categories.intersection(GROUP_CATEGORIES):
            continue
        title = page["title"]
        declared_name = first_field(infobox, "name") or title
        aliases = sorted({title, declared_name}, key=str.casefold)
        section_base_candidates = section_candidates(content)
        infobox_candidates = infobox_base_candidates(infobox.get("bases") or infobox.get("base"))
        candidate_sources: dict[str, set[str]] = defaultdict(set)
        for candidate in section_base_candidates:
            candidate_sources[candidate].add("base/build section")
        for candidate in infobox_candidates:
            candidate_sources[candidate].add("Infobox group bases")
        if classify(categories) in {"Build", "Mixed"} and normalize(title) in location_index:
            candidate_sources[title].add("building-group title equals location")
        base_candidates = sorted(candidate_sources, key=str.casefold)
        all_links = wiki_links(content)
        exact_build_matches: list[dict[str, Any]] = []
        near_build_matches: list[dict[str, Any]] = []
        rejected_group_identity_candidates: list[dict[str, Any]] = []
        for candidate in base_candidates:
            evidence = sorted(candidate_sources[candidate])
            candidate_key = normalize(candidate)
            if candidate_key in group_identity_keys and evidence == ["base/build section"]:
                rejected = {
                    "candidate": candidate,
                    "evidence": evidence,
                    "reason": "candidate is a known group identity without an explicit infobox base declaration",
                }
                rejected_hits = location_index.get(candidate_key, [])
                if rejected_hits:
                    rejected_group_identity_candidates.extend({**rejected, **hit} for hit in rejected_hits)
                else:
                    rejected_group_identity_candidates.append(rejected)
                continue
            hits = location_index.get(candidate_key, [])
            if not hits:
                hits = iteration_location_index.get(normalize_iteration(candidate), [])
                if len(hits) == 1:
                    evidence.append("trailing Roman/Arabic iteration equivalence")
            if len(hits) == 1:
                exact_build_matches.append({
                    "candidate": candidate,
                    "evidence": evidence,
                    **hits[0],
                })
                continue
            if "Infobox group bases" not in evidence and "building-group title equals location" not in evidence:
                continue
            ranked = sorted((
                (difflib.SequenceMatcher(None, candidate_key, normalize(location["Name"])).ratio(), location)
                for location in locations if candidate_key and normalize(location["Name"])
            ), key=lambda item: item[0], reverse=True)[:2]
            if ranked and ranked[0][0] >= 0.84:
                runner_up = ranked[1][0] if len(ranked) > 1 else 0.0
                if ranked[0][0] - runner_up >= 0.03:
                    near_build_matches.append({
                        "candidate": candidate,
                        "evidence": evidence,
                        "similarity": round(ranked[0][0], 4),
                        "runnerUpSimilarity": round(runner_up, 4),
                        **ranked[0][1],
                    })
        mention_matches: list[dict[str, Any]] = []
        for candidate in all_links:
            hits = location_index.get(normalize(candidate), [])
            if len(hits) == 1:
                mention_matches.append({"candidate": candidate, **hits[0]})
        group_hits: list[AtlasGroup] = []
        for alias in aliases:
            group_hits.extend(atlas_group_index.get(normalize(alias), []))
            group_hits.extend(atlas_group_wiki_index.get(normalize(alias), []))
            reviewed_target = REVIEWED_GROUP_ALIASES.get(alias)
            if reviewed_target:
                group_hits.extend(atlas_group_index.get(normalize(reviewed_target), []))
        group_hits = list({hit.id: hit for hit in group_hits}.values())
        urls = external_urls(content)
        discord = next((url for url in urls if "discord.gg/" in url or "discord.com/invite/" in url), None)
        website = next((url for url in urls if "discord" not in url and "youtube" not in url and "youtu.be" not in url), None)
        image_name = infobox_image_name(infobox, "image", "logo", "flag")
        image_title = image_name if image_name and image_name.casefold().startswith("file:") else (
            f"File:{image_name}" if image_name else None
        )
        logo = page.get("original", {}).get("source") or page.get("thumbnail", {}).get("source")
        status = first_field(infobox, "status")
        disbanded = first_field(infobox, "date_disbanded", "disbanded")
        if not status and disbanded:
            status = "Disbanded"
        if not status and "Disbanded groups" in categories:
            status = "Disbanded"
        record = {
            "pageId": page.get("pageid"), "title": title, "name": declared_name,
            "publicUrl": public_url(public_base, title), "apiCanonicalUrl": page.get("canonicalurl"),
            "revisionId": revision.get("revid") or page.get("lastrevid"),
            "revisionUtc": revision.get("timestamp"), "pageLength": page.get("length", len(content)),
            "categories": sorted(categories), "type": classify(categories),
            "founded": first_field(infobox, "date_founded", "founded", "foundation"),
            "disbanded": disbanded, "status": status,
            "leaders": first_field(infobox, "leader", "leaders", "owner", "owners"),
            "founders": first_field(infobox, "founder", "founders"),
            "members": first_field(infobox, "members", "member_count"),
            "declaredBases": first_field(infobox, "bases", "base"),
            "events": first_field(infobox, "events"), "intro": page.get("extract") or None,
            "imageName": image_name, "logoUrl": logo,
            "logoSourceUrl": public_url(public_base, image_title) if image_title else None,
            "websiteCandidate": website, "discordCandidate": discord,
            "baseSectionCandidates": section_base_candidates,
            "infoboxBaseCandidates": infobox_candidates,
            "exactAtlasBuildMatches": exact_build_matches,
            "nearAtlasBuildMatches": near_build_matches,
            "rejectedGroupIdentityCandidates": rejected_group_identity_candidates,
            "allLinkedAtlasLocations": mention_matches,
            "atlasGroups": [asdict(group) for group in group_hits],
        }
        records.append(record)

    matched_group_ids = {group["id"] for record in records for group in record["atlasGroups"]}
    atlas_unmatched = [asdict(group) for group in atlas_groups if group.id not in matched_group_ids]
    missing = [record for record in records if not record["atlasGroups"]]
    exact_links = [
        {"group": record["name"], "groupUrl": record["publicUrl"], "revisionId": record["revisionId"],
         "groupIds": [group["id"] for group in record["atlasGroups"]], **match}
        for record in records for match in record["exactAtlasBuildMatches"]
    ]
    declared_links = [item for item in exact_links if
                      "Infobox group bases" in item["evidence"] or
                      "building-group title equals location" in item["evidence"]]
    near_links = [
        {"group": record["name"], "groupUrl": record["publicUrl"], "revisionId": record["revisionId"], **match}
        for record in records for match in record["nearAtlasBuildMatches"]
    ]
    rejected_group_identity_links = [
        {"group": record["name"], "groupUrl": record["publicUrl"], "revisionId": record["revisionId"], **match}
        for record in records for match in record["rejectedGroupIdentityCandidates"]
    ]
    deterministic_pairs = {
        (int(item["Rowid"]), int(item["groupIds"][0]))
        for item in exact_links
        if len(item["groupIds"]) == 1 and any(source in item["evidence"] for source in (
            "Infobox group bases", "building-group title equals location", "base/build section"))
    }
    strong_pairs = {
        (int(item["Rowid"]), int(item["groupIds"][0]))
        for item in exact_links
        if len(item["groupIds"]) == 1 and "Infobox group bases" in item["evidence"]
    }
    reviewed_group_ids = {group.id for group in atlas_groups}
    comparable_reviewed = {pair for pair in reviewed_links if pair[1] in reviewed_group_ids}
    agreed = deterministic_pairs & comparable_reviewed
    strong_agreed = strong_pairs & comparable_reviewed
    new_candidates = deterministic_pairs - comparable_reviewed
    reviewed_not_rediscovered = comparable_reviewed - deterministic_pairs
    benchmark = {
        "reviewedLinks": len(comparable_reviewed),
        "deterministicCandidates": len(deterministic_pairs),
        "strongAutoApplyCandidates": len(strong_pairs),
        "agreedWithReviewed": len(agreed),
        "strongAgreedWithReviewed": len(strong_agreed),
        "newReviewCandidates": len(new_candidates),
        "reviewedNotRediscovered": len(reviewed_not_rediscovered),
        "candidateAgreementRate": round(len(agreed) / len(deterministic_pairs), 4) if deterministic_pairs else 1.0,
        "strongCandidateAgreementRate": round(len(strong_agreed) / len(strong_pairs), 4) if strong_pairs else 1.0,
        "reviewedCoverageRate": round(len(agreed) / len(comparable_reviewed), 4) if comparable_reviewed else 1.0,
        "newCandidatePairs": [
            {"locationId": location_id, "groupId": group_id}
            for location_id, group_id in sorted(new_candidates)
        ],
        "reviewedPairsNotRediscovered": [
            {"locationId": location_id, "groupId": group_id}
            for location_id, group_id in sorted(reviewed_not_rediscovered)
        ],
    }
    return {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "policy": "Read-only audit. Wiki claims and exact names are evidence, never authorization. No database writes.",
        "reviewedGroupAliases": [
            {"wikiIdentity": alias, "atlasIdentity": target}
            for alias, target in sorted(REVIEWED_GROUP_ALIASES.items(), key=lambda item: item[0].casefold())
        ],
        "wikiApi": api, "publicWikiBase": public_base,
        "wikiGenerator": site.get("generator"), "wikiSitename": site.get("sitename"),
        "stats": {
            "auditedArticles": len(records), "atlasGroups": len(atlas_groups),
            "matchedAtlasGroups": len(matched_group_ids), "unmatchedAtlasGroups": len(atlas_unmatched),
            "wikiGroupsNotInAtlas": len(missing), "exactBuildCandidates": len(exact_links),
            "uniqueExactBuildLocations": len({item["Rowid"] for item in exact_links}),
            "declaredBuildCandidates": len(declared_links),
            "uniqueDeclaredBuildLocations": len({item["Rowid"] for item in declared_links}),
            "nearBuildCandidates": len(near_links),
            "rejectedGroupIdentityCandidates": len(rejected_group_identity_links),
            "uniqueRejectedGroupIdentityLocations": len({item["Rowid"] for item in rejected_group_identity_links if "Rowid" in item}),
        },
        "atlasGroupsWithoutExactWikiArticle": atlas_unmatched,
        "reviewedRegressionBenchmark": benchmark,
        "exactBuildCandidates": exact_links,
        "nearBuildCandidates": near_links,
        "rejectedGroupIdentityCandidates": rejected_group_identity_links,
        "articles": sorted(records, key=lambda item: item["name"].casefold()),
    }


def markdown(report: dict[str, Any]) -> str:
    stats = report["stats"]
    lines = [
        "# 2b2t Wiki group audit", "",
        f"Generated `{report['generatedUtc']}` from MediaWiki `{report['wikiGenerator']}`.", "",
        "> This is a source and matching ledger, not an automatic import. Wiki claims can be incomplete, self-authored, or stale. Exact names still require historical-context review before ownership is published.", "",
        "## Coverage", "",
        "| Metric | Count |", "| --- | ---: |",
        f"| Main-namespace group articles audited | {stats['auditedArticles']} |",
        f"| Atlas groups compared | {stats['atlasGroups']} |",
        f"| Atlas groups with an exact wiki article | {stats['matchedAtlasGroups']} |",
        f"| Wiki group articles not represented in Atlas | {stats['wikiGroupsNotInAtlas']} |",
        f"| Exact location names found in all reviewed evidence | {stats['exactBuildCandidates']} |",
        f"| Unique Atlas locations in those candidates | {stats['uniqueExactBuildLocations']} |",
        f"| Candidates declared by an infobox/title identity | {stats['declaredBuildCandidates']} |",
        f"| Unique Atlas locations in the declared set | {stats['uniqueDeclaredBuildLocations']} |",
        f"| Review-only near-name candidates | {stats['nearBuildCandidates']} |",
        f"| Group-identity candidates held from attribution | {stats['rejectedGroupIdentityCandidates']} |",
        f"| Unique Atlas locations held by the identity guard | {stats['uniqueRejectedGroupIdentityLocations']} |", "",
        "## Atlas groups without an exact wiki article", "",
    ]
    unmatched = report["atlasGroupsWithoutExactWikiArticle"]
    lines.extend([f"- `{item['name']}` (Atlas group {item['id']})" for item in unmatched] or ["- None"])
    lines += ["", "## Existing Atlas groups", "",
              "| Wiki group | Type | Founded | Status | Exact base/build candidates | Revision |",
              "| --- | --- | --- | --- | ---: | ---: |"]
    for record in report["articles"]:
        if not record["atlasGroups"]:
            continue
        lines.append(
            f"| [{record['name']}]({record['publicUrl']}) | {record['type']} | "
            f"{record['founded'] or '—'} | {record['status'] or '—'} | "
            f"{len(record['exactAtlasBuildMatches'])} | {record['revisionId'] or '—'} |"
        )
    lines += ["", "## Exact-name build attribution candidates", "",
              "These come from an `Infobox group` base list, a building-group title matching an Atlas location, or links/headings inside sections explicitly labelled as bases, builds, projects, outposts, or lodges. The evidence column preserves that distinction. All remain candidates for human review, not automatic proof of ownership.", "",
              "| Group | Atlas location | Evidence | Coordinates | Source revision |",
              "| --- | --- | --- | --- | ---: |"]
    for item in report["exactBuildCandidates"]:
        lines.append(
            f"| [{item['group']}]({item['groupUrl']}) | {item['Name']} (ID {item['Rowid']}) | "
            f"{', '.join(item['evidence'])} | "
            f"dim {item['Dimension']}; {item['X']}, {item['Z']} | {item['revisionId']} |"
        )
    lines += ["", "## Review-only near-name build candidates", "",
              "These are unique high-similarity suggestions from explicit infobox/title evidence. They are never automatic: typos, dropped articles, and numbered iterations can identify different locations.", "",
              "| Group | Wiki candidate | Atlas location | Similarity | Source revision |",
              "| --- | --- | --- | ---: | ---: |"]
    for item in report["nearBuildCandidates"]:
        lines.append(
            f"| [{item['group']}]({item['groupUrl']}) | {item['candidate']} | "
            f"{item['Name']} (ID {item['Rowid']}) | {item['similarity']:.3f} | {item['revisionId']} |"
        )
    lines += ["", "## Group-identity candidates held from attribution", "",
              "These names identify another known Atlas group and appeared only in a base/build section. They remain visible for review but are excluded from build ownership unless an explicit infobox base declaration corroborates them.", "",
              "| Source group | Held identity | Atlas location | Evidence | Source revision |",
              "| --- | --- | --- | --- | ---: |"]
    for item in report["rejectedGroupIdentityCandidates"]:
        atlas_location = (
            f"{item['Name']} (ID {item['Rowid']})"
            if "Rowid" in item else "No exact Atlas location"
        )
        lines.append(
            f"| [{item['group']}]({item['groupUrl']}) | {item['candidate']} | "
            f"{atlas_location} | {', '.join(item['evidence'])} | {item['revisionId']} |"
        )
    lines += ["", "## Wiki groups not currently represented in Atlas", "",
              "Sorted by article size as a rough review priority; size is not a reliability score.", "",
              "| Group | Type | Founded | Status | Page bytes | Revision |",
              "| --- | --- | --- | --- | ---: | ---: |"]
    missing = [item for item in report["articles"] if not item["atlasGroups"]]
    for record in sorted(missing, key=lambda item: (-item["pageLength"], item["name"].casefold())):
        lines.append(
            f"| [{record['name']}]({record['publicUrl']}) | {record['type']} | "
            f"{record['founded'] or '—'} | {record['status'] or '—'} | "
            f"{record['pageLength']} | {record['revisionId'] or '—'} |"
        )
    lines += ["", "## Review rules", "",
              "- Preserve the page title, revision ID, and evidence URL for every accepted field.",
              "- Do not infer ownership from proximity, render overlap, or an incidental article link.",
              "- Treat active/private coordinates and stash disclosures as non-publishable.",
              "- Resolve aliases and same-name groups before creating a new group.",
              "- Verify Discord invites and websites independently; historical links are not assumed active.", ""]
    benchmark = report.get("reviewedRegressionBenchmark", {})
    lines += ["## Reviewed-regression benchmark", "",
              "This compares deterministic explicit-evidence candidates with the Atlas links already reviewed by an operator. It is a regression signal, not a claim that every existing link must appear on the wiki.", "",
              f"- Reviewed links compared: {benchmark.get('reviewedLinks', 0)}",
              f"- Deterministic candidates: {benchmark.get('deterministicCandidates', 0)}",
              f"- Strong infobox auto-apply candidates: {benchmark.get('strongAutoApplyCandidates', 0)}",
              f"- Candidate agreement: {benchmark.get('candidateAgreementRate', 0):.1%}",
              f"- Strong-candidate agreement: {benchmark.get('strongCandidateAgreementRate', 0):.1%}",
              f"- Reviewed-link coverage: {benchmark.get('reviewedCoverageRate', 0):.1%}",
              f"- New review candidates: {benchmark.get('newReviewCandidates', 0)}", ""]
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--wiki-api", default=DEFAULT_API)
    parser.add_argument("--public-wiki-base", default=DEFAULT_PUBLIC_WIKI)
    parser.add_argument("--atlas-db", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--docs-report", type=Path)
    parser.add_argument("--runtime-index", type=Path,
                        help="Optional atomically replaced runtime copy consumed by Atlas AI enrichment.")
    args = parser.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    articles, site = fetch_articles(args.wiki_api)
    atlas_groups, locations, reviewed_links = load_atlas(args.atlas_db.resolve())
    report = make_report(articles, atlas_groups, locations, reviewed_links,
                         args.wiki_api, args.public_wiki_base, site)
    json_path = args.output_dir / "2b2t-wiki-group-audit.json"
    md_path = args.output_dir / "2b2t-wiki-group-audit.md"
    serialized = json.dumps(report, ensure_ascii=False, indent=2)
    json_path.write_text(serialized, encoding="utf-8")
    report_markdown = markdown(report)
    md_path.write_text(report_markdown, encoding="utf-8")
    if args.docs_report:
        args.docs_report.parent.mkdir(parents=True, exist_ok=True)
        args.docs_report.write_text(report_markdown, encoding="utf-8")
    if args.runtime_index:
        runtime_index = args.runtime_index.resolve()
        runtime_index.parent.mkdir(parents=True, exist_ok=True)
        temporary = runtime_index.with_name(runtime_index.name + ".tmp")
        temporary.write_text(serialized, encoding="utf-8")
        temporary.replace(runtime_index)
    print(json.dumps({"json": str(json_path), "markdown": str(md_path), **report["stats"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
