#!/usr/bin/env python3
"""Print a location's attachment links, captions and source credits."""
import argparse
import json
import os
from urllib.parse import urlencode
from urllib.request import Request, urlopen


def attachments(base_url, location_id):
    offset = 0
    while True:
        query = urlencode({"locationId": location_id, "limit": 100, "offset": offset})
        request = Request(base_url.rstrip("/") + "/api/attachments?" + query,
                          headers={"User-Agent": "Atlas-location-media-example/1.0"})
        with urlopen(request, timeout=30) as response:
            page = json.load(response)
        if not isinstance(page, list):
            raise ValueError("Expected an attachment array")
        yield from page
        if len(page) < 100:
            return
        offset += len(page)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("location_id", type=int)
    parser.add_argument("--base-url", default=os.getenv("ATLAS_API_BASE_URL", "https://api.blackportal.cloud"))
    args = parser.parse_args()
    for item in attachments(args.base_url, args.location_id):
        print(f"[{item.get('mediaType') or 'Link'}] {item.get('fileName') or 'Untitled'}")
        print(item.get("path") or "")
        for key in ("caption", "attribution", "sourceUrl"):
            if item.get(key):
                print(f"  {key}: {item[key]}")
        print()


if __name__ == "__main__":
    main()
