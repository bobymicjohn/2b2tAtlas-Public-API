"""Offline regressions for the public MCP documentation contract checker."""

import copy
import unittest

from test_documentation import check_example_argument_names, check_mcp_contract


def fixture():
    return [{
        "name": "search_example",
        "inputSchema": {
            "type": "object",
            "properties": {
                "query": {
                    "type": "object",
                    "properties": {
                        "kinds": {
                            "type": ["array", "null"],
                            "items": {"type": "string", "enum": ["sign", "storage"]},
                        },
                        "limit": {"type": "integer", "default": 5, "minimum": 1, "maximum": 50},
                        "description": {"type": "string", "description": "Example text, at most 100 characters."},
                    },
                    "required": ["kinds", "limit"],
                    "additionalProperties": False,
                },
            },
            "required": ["query"],
        },
    }]


class McpContractTests(unittest.TestCase):
    def test_unchanged_schema_passes_without_live_request(self):
        check_mcp_contract(fixture(), copy.deepcopy(fixture()))

    def test_nested_new_argument_rejected_even_with_same_tool_name(self):
        live = fixture()
        live[0]["inputSchema"]["properties"]["query"]["properties"]["spawnerMob"] = {"type": ["string", "null"]}
        with self.assertRaisesRegex(AssertionError, r"query\.properties\.spawnerMob: missing"):
            check_mcp_contract(fixture(), live)

    def test_type_default_bounds_and_required_drift_rejected(self):
        for keyword, value in [("type", "number"), ("default", 25), ("minimum", 2), ("maximum", 100)]:
            with self.subTest(keyword=keyword):
                live = fixture()
                live[0]["inputSchema"]["properties"]["query"]["properties"]["limit"][keyword] = value
                with self.assertRaisesRegex(AssertionError, rf"limit\.{keyword}"):
                    check_mcp_contract(fixture(), live)
        live = fixture()
        live[0]["inputSchema"]["properties"]["query"]["required"] = ["kinds"]
        with self.assertRaisesRegex(AssertionError, r"query\.required"):
            check_mcp_contract(fixture(), live)

    def test_nested_array_item_enum_drift_rejected(self):
        live = fixture()
        live[0]["inputSchema"]["properties"]["query"]["properties"]["kinds"]["items"]["enum"].append("spawner")
        with self.assertRaisesRegex(AssertionError, r"kinds\.items\.enum"):
            check_mcp_contract(fixture(), live)

    def test_prose_limit_change_rejected(self):
        live = fixture()
        live[0]["inputSchema"]["properties"]["query"]["properties"]["description"]["description"] = "At most 200 characters."
        with self.assertRaisesRegex(AssertionError, r"properties\.description\.description"):
            check_mcp_contract(fixture(), live)

    def test_removed_argument_and_additional_property_policy_rejected(self):
        live = fixture()
        del live[0]["inputSchema"]["properties"]["query"]["properties"]["description"]
        with self.assertRaisesRegex(AssertionError, r"description: absent"):
            check_mcp_contract(fixture(), live)
        live = fixture()
        live[0]["inputSchema"]["properties"]["query"]["additionalProperties"] = True
        with self.assertRaisesRegex(AssertionError, r"query\.additionalProperties"):
            check_mcp_contract(fixture(), live)

    def test_semantic_set_order_does_not_fail(self):
        live = fixture()
        query = live[0]["inputSchema"]["properties"]["query"]
        query["required"].reverse()
        query["properties"]["kinds"]["type"].reverse()
        query["properties"]["kinds"]["items"]["enum"].reverse()
        check_mcp_contract(fixture(), live)

    def test_literal_default_array_order_is_preserved(self):
        documented = fixture()
        documented[0]["inputSchema"]["properties"]["query"]["default"] = {"required": ["first", "second"]}
        live = copy.deepcopy(documented)
        live[0]["inputSchema"]["properties"]["query"]["default"]["required"].reverse()
        with self.assertRaisesRegex(AssertionError, r"query\.default\.required"):
            check_mcp_contract(documented, live)

    def test_boolean_is_not_equal_to_numeric_default(self):
        documented = fixture()
        documented[0]["inputSchema"]["properties"]["query"]["default"] = [1]
        live = copy.deepcopy(documented)
        live[0]["inputSchema"]["properties"]["query"]["default"] = [True]
        with self.assertRaisesRegex(AssertionError, r"query\.default\[0\]"):
            check_mcp_contract(documented, live)

    def test_missing_schema_and_duplicate_names_fail(self):
        with self.assertRaisesRegex(AssertionError, "missing documented inputSchema"):
            check_mcp_contract([{"name": "search_example"}], fixture())
        with self.assertRaisesRegex(AssertionError, "Duplicate documented"):
            check_mcp_contract(fixture() * 2, fixture())
        with self.assertRaisesRegex(AssertionError, "Duplicate live"):
            check_mcp_contract(fixture(), fixture() * 2)

    def test_new_or_removed_tool_fails(self):
        with self.assertRaisesRegex(AssertionError, "MCP tool names changed"):
            check_mcp_contract(fixture(), [])
        live = fixture()
        live.append({"name": "another_tool", "inputSchema": {"type": "object"}})
        with self.assertRaisesRegex(AssertionError, "another_tool"):
            check_mcp_contract(fixture(), live)

    def test_example_argument_names_and_required_fields(self):
        schema = fixture()[0]["inputSchema"]
        check_example_argument_names({"query": {"kinds": ["sign"], "limit": 5}}, schema, "example")
        with self.assertRaisesRegex(AssertionError, "unknown example arguments"):
            check_example_argument_names({"query": {"kinds": [], "limit": 5, "typo": True}}, schema, "example")
        with self.assertRaisesRegex(AssertionError, "missing required example arguments"):
            check_example_argument_names({"query": {"kinds": []}}, schema, "example")


if __name__ == "__main__":
    unittest.main()
