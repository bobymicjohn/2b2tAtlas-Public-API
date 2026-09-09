import importlib.util
from pathlib import Path
import sys
import unittest


SCRIPT = Path(__file__).parents[1] / "audit-2b2t-wiki-groups.py"
SPEC = importlib.util.spec_from_file_location("group_audit", SCRIPT)
assert SPEC and SPEC.loader
audit = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = audit
SPEC.loader.exec_module(audit)


class SectionCandidateTests(unittest.TestCase):
    def test_comparison_and_former_member_projects_are_not_ownership(self):
        text = """
== Projects ==
# DGA Capital - The capital was like [[Wrath Outpost]] for the DGA.
# Base Unity - A shared alliance base.
== Post-Crimson Projects Involving Many Crimson Members ==
Former members participated in [[The Drain]].
"""
        self.assertEqual(["Base Unity", "DGA Capital"], audit.section_candidates(text))

    def test_builder_links_are_excluded_from_build_section(self):
        text = """
== Builds ==
=== The Valley of Crafting Tables ===
The project was near [[Valley of Wheat]] and helped by [[The Emperium]].
==== Builders ====
* [[5K5K]]
"""
        self.assertEqual(["The Valley of Crafting Tables"], audit.section_candidates(text))

    def test_memorial_link_does_not_replace_plain_list_subject(self):
        text = """
== Bases ==
-<u>La Capital</u> (2017-2018) was the founding base.
-<u>Eagles Nest</u> (2019) had a church commemorating [[Valkyria]].
"""
        self.assertEqual(["Eagles Nest", "La Capital"], audit.section_candidates(text))

    def test_specific_rebuild_heading_and_main_article_are_kept(self):
        text = """
=== Base Nostalgia Rebuild ===
The group doubled in size when [[Some Player]] joined.
=== Cum Zone II Base ===
Main Article: [[Cum Zone II]]
"""
        self.assertEqual(
            ["Base Nostalgia", "Cum Zone II", "Cum Zone II Base"],
            audit.section_candidates(text),
        )


class ReviewedGroupAliasTests(unittest.TestCase):
    def test_society_alias_resolves_to_existing_society_project_group(self):
        page = {
            "pageid": 5080,
            "title": "The Society",
            "lastrevid": 80948,
            "length": 200,
            "categories": [{"title": "Category:Groups"}],
            "revisions": [{
                "revid": 80948,
                "timestamp": "2025-01-22T16:50:20Z",
                "slots": {"main": {"content": "{{Infobox group|name=The Society|date_founded=June 6th, 2017}}\nThe Society Project, or The Society, is an organization."}},
            }],
        }
        group = audit.AtlasGroup(54, "The Society Project", "Other", None, None)

        report = audit.make_report(
            [page], [group], [], set(),
            "https://2b2t.miraheze.org/w/api.php",
            "https://2b2t.wikioasis.org/wiki/",
            {"generator": "MediaWiki", "sitename": "2b2t Wiki"},
        )

        self.assertEqual(
            [{"wikiIdentity": "The Society", "atlasIdentity": "The Society Project"}],
            report["reviewedGroupAliases"],
        )
        self.assertEqual([54], [item["id"] for item in report["articles"][0]["atlasGroups"]])
        self.assertEqual([], report["atlasGroupsWithoutExactWikiArticle"])


class GroupIdentityLocationGuardTests(unittest.TestCase):
    def test_group_name_in_another_groups_base_section_is_not_a_build(self):
        page = {
            "pageid": 4102,
            "title": "Vortex Coalition",
            "categories": [{"title": "Category:Building groups"}],
            "revisions": [{
                "revid": 85030,
                "timestamp": "2026-08-23T06:21:00Z",
                "slots": {"main": {"content": """{{Infobox group
| name = Vortex Coalition
}}
== Bases ==
* [[DonFuer]]
"""}},
            }],
        }
        groups = [
            audit.AtlasGroup(3, "Vortex Coalition", "Other", None, "https://2b2t.wikioasis.org/wiki/Vortex_Coalition"),
            audit.AtlasGroup(15, "DonFuer", "Build", None, "https://2b2t.wikioasis.org/wiki/DonFuer"),
        ]
        locations = [{"Rowid": 397, "Name": "DonFuer", "Dimension": 0, "X": 1266360, "Z": 1646759}]

        report = audit.make_report(
            [page], groups, locations, set(),
            "https://2b2t.miraheze.org/w/api.php",
            "https://2b2t.wikioasis.org/wiki/",
            {"generator": "MediaWiki", "sitename": "2b2t Wiki"},
        )

        article = report["articles"][0]
        self.assertEqual([], article["exactAtlasBuildMatches"])
        self.assertEqual("DonFuer", article["rejectedGroupIdentityCandidates"][0]["candidate"])
        self.assertEqual([], report["exactBuildCandidates"])
        self.assertEqual(1, report["stats"]["rejectedGroupIdentityCandidates"])
        self.assertEqual(1, report["stats"]["uniqueRejectedGroupIdentityLocations"])
        self.assertEqual("DonFuer", report["rejectedGroupIdentityCandidates"][0]["candidate"])
        self.assertIn("Group-identity candidates held from attribution", audit.markdown(report))


if __name__ == "__main__":
    unittest.main()
