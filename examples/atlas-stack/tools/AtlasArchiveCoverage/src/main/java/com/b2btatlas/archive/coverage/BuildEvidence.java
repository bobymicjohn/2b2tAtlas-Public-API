package com.b2btatlas.archive.coverage;

/** Identical evidence rules for saved terrain and live chunks. Counts are hints, not ownership. */
final class BuildEvidence {
    static void countNamedBlockEvidence(String name, int count, int[] totals) {
            String path = name.startsWith("minecraft:") ? name.substring(10) : name;
            if (path.equals("air") || path.equals("cave_air") || path.equals("void_air")) return;
            totals[0] += count;
            if (isArtificialBlock(path)) totals[1] += count;
            if (isStrongArtificialBlock(path)) totals[2] += count;
        }

    static boolean isArtificialBlock(String path) {
            return isStrongArtificialBlock(path) ||
                containsAny(path, "planks", "bricks", "glass", "pane", "concrete", "wool", "carpet",
                    "slab", "stairs", "fence", "wall", "door", "trapdoor", "torch", "lantern", "chain",
                    "iron_bars", "ladder", "scaffolding", "chest", "barrel", "furnace", "crafting_table",
                    "bookshelf", "farmland", "polished", "cut_", "chiseled", "glazed_terracotta",
                    "copper_bulb", "copper_grate", "waxed_", "purpur", "quartz_block", "quartz_pillar");
        }

    static boolean isStrongArtificialBlock(String path) {
            return equalsAny(path, "beacon", "conduit", "enchanting_table", "ender_chest", "respawn_anchor",
                    "lodestone", "jukebox", "note_block", "brewing_stand", "grindstone", "smithing_table",
                    "stonecutter", "loom", "cartography_table", "fletching_table", "blast_furnace", "smoker",
                    "dispenser", "dropper", "hopper", "piston", "sticky_piston", "piston_head", "moving_piston",
                    "observer", "comparator", "repeater", "daylight_detector", "lever", "tripwire_hook", "rail",
                    "powered_rail", "detector_rail", "activator_rail", "redstone_wire", "redstone_torch",
                    "redstone_wall_torch", "redstone_lamp", "redstone_block", "target", "tnt", "anvil",
                    "chipped_anvil", "damaged_anvil") ||
                endsWithAny(path, "_shulker_box", "_banner", "_wall_banner", "_sign", "_wall_sign",
                    "_hanging_sign", "_wall_hanging_sign", "_bed", "_button", "_pressure_plate");
        }

    static boolean containsAny(String value, String... needles) {
            for (String needle : needles) {
                if (value.contains(needle)) return true;
            }
            return false;
        }

    static boolean equalsAny(String value, String... candidates) {
            for (String candidate : candidates) {
                if (value.equals(candidate)) return true;
            }
            return false;
        }

    static boolean endsWithAny(String value, String... suffixes) {
            for (String suffix : suffixes) {
                if (value.endsWith(suffix)) return true;
            }
            return false;
        }

}
