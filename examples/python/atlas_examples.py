#!/usr/bin/env python3
"""Dependency-free 2b2tAtlas public API examples."""

from __future__ import annotations

import json
import os
import sys
import urllib.parse
import urllib.request

API = os.environ.get("ATLAS_API_BASE_URL", "https://api.blackportal.cloud").rstrip("/")


def get(path: str, params: dict[str, object] | None = None):
    query = "?" + urllib.parse.urlencode(params) if params else ""
    request = urllib.request.Request(
        API + path + query,
        headers={
            "Accept": "application/json",
            "User-Agent": "2b2tAtlas-API-Examples/1.0",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def normalize(value: str) -> str:
    return " ".join(value.casefold().split())


def main() -> int:
    search = " ".join(sys.argv[1:]) or "Mu Megabase"
    locations = get("/api/locations")
    needle = normalize(search)
    location = next((item for item in locations if normalize(item["name"]) == needle), None)
    location = location or next((item for item in locations if needle in normalize(item["name"])), None)
    if location is None:
        print(f"No location matched: {search}", file=sys.stderr)
        return 1

    print(
        f'{location["name"]} [{location["dimensionName"]}] '
        f'{location["x"]}, {location["y"]}, {location["z"]}'
    )
    print(location["interactiveUrl"])

    location_id = location["rowid"]
    warps = get("/api/warps", {"locationId": location_id, "limit": 1000})
    renders = get("/api/renders", {"locationId": location_id, "limit": 1000})
    print("warps:", [f'/warp {item["name"]}' for item in warps])
    print("renders:", [(item.get("worldDownloadDate"), item["apiUrl"]) for item in renders])

    if location.get("groups"):
        group = get(f'/api/groups/{location["groups"][0]["groupId"]}')
        print(f'{group["name"]}: {group["locationCount"]} builds, {group["highwayCount"]} highways')
        for build in group["locations"][:5]:
            print(f'  build: {build["name"]} ({build["role"]}) -> {build["locationInteractiveUrl"]}')

    highways = get("/api/highways")
    for highway in (item for item in highways if "+Z" in item["name"]):
        print(f'highway: {highway["name"]} | dimension {highway["dimension"]} | {highway["apiUrl"]}')

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
