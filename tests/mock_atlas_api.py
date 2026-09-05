#!/usr/bin/env python3
"""Small offline response-shape fixture used by repository CI; not an Atlas server implementation."""

from __future__ import annotations

import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse


LOCATION = {
    "rowid": 5,
    "name": "Mu Megabase",
    "description": "Historical fixture location.",
    "dimension": 0,
    "dimensionName": "Overworld",
    "x": 5168556,
    "y": 64,
    "z": 10320373,
    "canonicalUrl": "https://2b2tatlas.com/entities/locations/5/",
    "interactiveUrl": "https://2b2tatlas.com/location/5",
    "apiUrl": "https://api.blackportal.cloud/api/locations/5",
    "groups": [
        {
            "groupId": 8,
            "groupName": "Nether Highway Group (NHG)",
            "role": "Builder",
            "groupApiUrl": "https://api.blackportal.cloud/api/groups/8",
        }
    ],
    "warps": [],
    "renders": [],
    "attachments": [],
}

GROUP_SUMMARY = {
    "id": 8,
    "name": "Nether Highway Group (NHG)",
    "aliases": ["NHG"],
    "description": "Historical fixture group.",
    "locationCount": 1,
    "highwayCount": 1,
    "canonicalUrl": "https://2b2tatlas.com/entities/groups/8/",
    "interactiveUrl": "https://2b2tatlas.com/group/8",
    "apiUrl": "https://api.blackportal.cloud/api/groups/8",
}

GROUP_DETAIL = {
    **GROUP_SUMMARY,
    "locations": [
        {
            "locationId": 5,
            "name": "Mu Megabase",
            "role": "Builder",
            "dimension": 0,
            "x": 5168556,
            "z": 10320373,
            "renderCount": 1,
            "locationInteractiveUrl": "https://2b2tatlas.com/location/5",
        }
    ],
    "highways": [
        {
            "highwayId": 1,
            "name": "+X Highway",
            "role": "Historical builder",
            "dimension": 1,
            "highwayApiUrl": "https://api.blackportal.cloud/api/highways/1",
            "mapUrl": "https://2b2tatlas.com/map?dimension=nether",
        }
    ],
}

WARP = {
    "id": 145,
    "locationRowid": 5,
    "locationName": "Mu Megabase",
    "name": "Mu_2020-03-02",
    "worldDownloadDate": "2020-03-02",
    "source": "fixture",
    "apiUrl": "https://api.blackportal.cloud/api/warps/145",
}

RENDER = {
    "renderId": 38,
    "locationId": 5,
    "locationName": "Mu Megabase",
    "name": "Mu Megabase",
    "dimension": 0,
    "worldDownloadDate": "2020-07-23",
    "tileUrlTemplate": "https://example.invalid/tiles/{dn}/{z}/{y}/{x}.png",
    "minX": 5168000,
    "minZ": 10320000,
    "maxXExclusive": 5169000,
    "maxZExclusive": 10321000,
    "maxNativeZoom": 9,
    "coordinateScheme": "atlas-sparse-v1",
    "apiUrl": "https://api.blackportal.cloud/api/renders/38",
}

HIGHWAY = {
    "id": 1,
    "name": "+Z Highway",
    "dimension": 1,
    "points": [{"x": 0, "z": 0}, {"x": 0, "z": 30000000}],
    "width": 6,
    "builderGroups": [],
    "apiUrl": "https://api.blackportal.cloud/api/highways/1",
    "mapUrl": "https://2b2tatlas.com/map?dimension=nether",
}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        request = urlparse(self.path)
        query = parse_qs(request.query)
        path = request.path.rstrip("/") or "/"

        if path == "/api":
            return self.send_json({"service": "2b2t Atlas API", "openApi": "/openapi/v1.json"})
        if path == "/openapi/v1.json":
            return self.send_json({"openapi": "3.0.1", "info": {"title": "2b2t Atlas API", "version": "fixture"}, "paths": {}})
        if path == "/api/locations":
            return self.send_json([LOCATION])
        if path == "/api/locations/5":
            return self.send_json(LOCATION)
        if path == "/api/groups":
            return self.send_json([GROUP_SUMMARY])
        if path == "/api/groups/7" or path == "/api/groups/8":
            return self.send_json(GROUP_DETAIL)
        if path == "/api/warps" or path == "/api/warps/145":
            rows = [WARP]
            if query.get("locationId", ["5"])[0] != "5":
                rows = []
            return self.send_json(rows if path == "/api/warps" else WARP)
        if path == "/api/renders" or path == "/api/renders/38" or path == "/api/locations/5/renders":
            rows = [] if int(query.get("offset", ["0"])[0]) > 0 else [RENDER]
            if path == "/api/renders/38":
                return self.send_json(RENDER)
            return self.send_json(rows)
        if path == "/api/attachments" or path == "/api/attachments/1":
            attachment = {
                "id": 1,
                "locationRowid": 5,
                "mediaType": "Image",
                "sourceUrl": "https://example.invalid/source",
                "caption": "Fixture image",
                "attribution": "Fixture",
                "apiUrl": "https://api.blackportal.cloud/api/attachments/1",
            }
            return self.send_json([attachment] if path == "/api/attachments" else attachment)
        if path == "/api/highways" or path == "/api/highways/1":
            return self.send_json([HIGHWAY] if path == "/api/highways" else HIGHWAY)
        if path == "/api/maprenders/catalog" or path == "/api/maprenders":
            return self.send_json([])
        self.send_error(404)

    def send_json(self, value) -> None:
        body = json.dumps(value, separators=(",", ":")).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format, *args) -> None:  # noqa: A002 - framework signature
        return


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 8765), Handler).serve_forever()
