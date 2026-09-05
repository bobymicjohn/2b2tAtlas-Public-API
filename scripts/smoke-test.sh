#!/usr/bin/env bash
set -euo pipefail

base="https://api.blackportal.cloud"
routes=(
  "/api"
  "/openapi/v1.json"
  "/api/locations/5"
  "/api/groups/7"
  "/api/warps?locationId=5&limit=1"
  "/api/renders?locationId=5&limit=1"
  "/api/attachments?limit=1"
  "/api/highways/1"
  "/api/maprenders/catalog"
)

for route in "${routes[@]}"; do
  curl --fail --silent --show-error --location \
    --header "Accept: application/json" \
    --user-agent "2b2tAtlas-API-Examples-Smoke/1.0" \
    "${base}${route}" >/dev/null
  echo "OK ${route}"
done
