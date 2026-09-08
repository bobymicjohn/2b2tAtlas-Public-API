#!/usr/bin/env bash
set -euo pipefail

base="${ATLAS_API_BASE_URL:-https://api.blackportal.cloud}"
base="${base%/}"
routes=(
  "/api"
  "/openapi/v1.json"
  "/api/locations/5"
  "/api/groups/7"
  "/api/warps?locationId=5&limit=1"
  "/api/renders?locationId=5&limit=1"
  "/api/renders/38/world-download"
  "/api/attachments?limit=1"
  "/api/highways/1"
  "/api/maprenders/catalog"
  "/api/nocom"
  "/api/nocom/periods?dimension=nether"
  "/api/nocom/highways?dimension=nether&direction=northeast"
)

for route in "${routes[@]}"; do
  curl --fail --silent --show-error --location \
    --header "Accept: application/json" \
    --user-agent "2b2tAtlas-Public-API-Smoke/1.0" \
    "${base}${route}" >/dev/null
  echo "OK ${route}"
done
