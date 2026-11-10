"""Check public documentation links, catalogs and optionally live discovery."""

import argparse
import json
import re
import urllib.request
from datetime import datetime
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCHEMA_MAPS = {"properties", "patternProperties", "$defs", "definitions", "dependentSchemas"}
SCHEMA_VALUES = {
    "items", "additionalProperties", "additionalItems", "contains", "propertyNames",
    "not", "if", "then", "else", "unevaluatedProperties", "unevaluatedItems",
}
SCHEMA_LISTS = {"allOf", "anyOf", "oneOf", "prefixItems"}


def normalize_route(route):
    return re.sub(r"\{\w+\}", "{id}", route.lower())


def schema_contract(schema):
    """Keep the whole input contract, ignoring order only for set-valued keywords.

    Descriptions are included: the server currently expresses some defaults and
    limits in prose rather than JSON Schema constraints. Literal defaults and
    property names are never rewritten as though they were schema keywords.
    """
    if not isinstance(schema, dict):
        return schema
    result = {}
    for key, value in schema.items():
        if key in SCHEMA_MAPS:
            result[key] = {name: schema_contract(child) for name, child in value.items()}
        elif key in SCHEMA_VALUES:
            result[key] = (
                [schema_contract(child) for child in value]
                if isinstance(value, list) else schema_contract(value)
            )
        elif key in SCHEMA_LISTS:
            result[key] = [schema_contract(child) for child in value]
        elif key in {"required", "type", "enum"} and isinstance(value, list):
            result[key] = sorted(value, key=lambda item: json.dumps(item, sort_keys=True))
        else:
            result[key] = value
    return result


def contract_differences(documented, live, path):
    """Return field paths so a changed argument is actionable in local output."""
    if isinstance(documented, dict) and isinstance(live, dict):
        differences = []
        for key in sorted(documented.keys() | live.keys()):
            field = f"{path}.{key}"
            if key not in documented:
                differences.append(f"{field}: missing from documentation")
            elif key not in live:
                differences.append(f"{field}: absent from live schema")
            else:
                differences.extend(contract_differences(documented[key], live[key], field))
        return differences
    if isinstance(documented, list) and isinstance(live, list):
        if len(documented) != len(live):
            return [f"{path}: documented {documented!r}; live {live!r}"]
        differences = []
        for index, (before, after) in enumerate(zip(documented, live)):
            differences.extend(contract_differences(before, after, f"{path}[{index}]"))
        return differences
    if type(documented) is not type(live) or documented != live:
        return [f"{path}: documented {documented!r}; live {live!r}"]
    return []


def check_mcp_contract(guide, live_tools):
    documented = {tool["name"]: tool for tool in guide}
    live = {tool["name"]: tool for tool in live_tools}
    assert len(documented) == len(guide), "Duplicate documented MCP tool names."
    assert len(live) == len(live_tools), "Duplicate live MCP tool names."
    assert documented.keys() == live.keys(), (
        f"MCP tool names changed; undocumented: {sorted(live.keys() - documented.keys())}; "
        f"absent live: {sorted(documented.keys() - live.keys())}"
    )
    differences = []
    for name in sorted(documented):
        assert "inputSchema" in documented[name], f"{name}: missing documented inputSchema"
        assert "inputSchema" in live[name], f"{name}: missing live inputSchema"
        differences.extend(contract_differences(
            schema_contract(documented[name]["inputSchema"]),
            schema_contract(live[name]["inputSchema"]),
            f"{name}.inputSchema",
        ))
    assert not differences, "MCP input contracts changed:\n" + "\n".join(differences)


def check_example_argument_names(value, schema, path):
    """Check supplied names and required fields; this is not a schema validator."""
    if isinstance(value, dict):
        properties = schema.get("properties", {})
        unknown = value.keys() - properties.keys()
        missing = set(schema.get("required", [])) - value.keys()
        assert not unknown, f"{path}: unknown example arguments {sorted(unknown)}"
        assert not missing, f"{path}: missing required example arguments {sorted(missing)}"
        for key, child in value.items():
            check_example_argument_names(child, properties[key], f"{path}.{key}")
    elif isinstance(value, list) and isinstance(schema.get("items"), dict):
        for index, child in enumerate(value):
            check_example_argument_names(child, schema["items"], f"{path}[{index}]")


def request(path, payload=None):
    headers = {"User-Agent": "AtlasPublicDocsCheck/1.0", "Accept": "application/json, text/event-stream"}
    if payload is not None:
        headers["Content-Type"] = "application/json"
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request("https://api.blackportal.cloud" + path, data=data, headers=headers)
    with urllib.request.urlopen(req, timeout=60) as response:
        raw = response.read().decode()
    if raw.startswith(("event:", "data:")):
        raw = next(line[6:] for line in raw.splitlines() if line.startswith("data: "))
    return json.loads(raw)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true")
    args = parser.parse_args()
    reference = json.loads((ROOT / "docs/reference/api-reference.json").read_text(encoding="utf-8"))
    guide = json.loads((ROOT / "docs/reference/mcp-tool-guide.json").read_text(encoding="utf-8"))
    operations = [op for group in reference["groups"] for op in group["operations"]]
    assert operations and guide, "Public route and MCP catalogs must not be empty."
    verified = datetime.strptime(reference["verifiedOn"], "%B %d, %Y")
    examples_verified = datetime.strptime(reference["examplesVerifiedOn"], "%B %d, %Y")
    assert examples_verified <= verified, "Response examples cannot postdate the contract check."
    assert len({normalize_route(op["route"]) for op in operations}) == len(operations)
    assert all(op["method"] == "GET" and op["isPublic"] for op in operations)
    mcp_text = (ROOT / "docs/MCP.md").read_text(encoding="utf-8")
    for tool in guide:
        assert tool["name"] in mcp_text, tool["name"]
        datetime.strptime(tool["schemaVerifiedOn"], "%B %d, %Y")
        assert isinstance(tool["inputSchema"], dict), tool["name"]
        check_example_argument_names(tool["arguments"], tool["inputSchema"], tool["name"])
    check_mcp_contract(guide, guide)
    for op in operations:
        for response in op["responses"]:
            if response.get("example"):
                json.loads(response["example"])
                example_date = datetime.strptime(
                    response.get("exampleVerifiedOn", reference["examplesVerifiedOn"]), "%B %d, %Y"
                )
                assert example_date <= verified, (op["route"], "Response example postdates the contract check.")

    errors = []
    links = 0
    for path in ROOT.rglob("*.md"):
        if any(part in {".git", "bin", "obj", "node_modules"} for part in path.relative_to(ROOT).parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        text = re.sub(r"(?ms)^```.*?^```[^\n]*", "", text)
        for match in re.finditer(r'\]\(([^\s)]+)(?:\s+"[^"]*")?\)', text):
            target = match[1].strip("<>").split("#")[0].split("?")[0]
            if not target or re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target) or target.startswith("/"):
                continue
            links += 1
            if not (path.parent / target).exists():
                errors.append(str(path.relative_to(ROOT)) + ": " + target)
    assert not errors, errors
    for path in [ROOT / "README.md", ROOT / "llms.txt", *list((ROOT / "docs").glob("*.md"))]:
        assert not re.search(r"(?:currently has 25 tools|18 tools total|33 current)", path.read_text(encoding="utf-8")), path

    if args.live:
        schema = request("/openapi/v1.json")
        published = {normalize_route(path): item["get"] for path, item in schema["paths"].items() if "get" in item}
        assert set(published) == {normalize_route(op["route"]) for op in operations}, "Public GET route inventory changed."
        for op in operations:
            actual = {(p["in"], p["name"], bool(p.get("required"))) for p in published[normalize_route(op["route"])].get("parameters", [])}
            documented = {(p["in"], p["name"], p["required"]) for p in op["parameters"]}
            assert actual <= documented, (op["route"], actual - documented)
            allowed = {("query", "sha256", False), ("header", "Range", False)} if op["route"].endswith("world-download.zip") else set()
            assert documented - actual <= allowed, (op["route"], documented - actual)
        tools = request("/mcp", {"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}})["result"]["tools"]
        check_mcp_contract(guide, tools)
    print(f"PASS: {len(operations)} public routes, {len(guide)} MCP input contracts, {links} local links; live discovery checked: {args.live}.")


if __name__ == "__main__":
    main()
