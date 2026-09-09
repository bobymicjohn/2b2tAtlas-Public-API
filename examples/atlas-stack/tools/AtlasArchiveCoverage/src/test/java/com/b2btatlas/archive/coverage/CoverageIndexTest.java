package com.b2btatlas.archive.coverage;

import java.util.*;

public final class CoverageIndexTest {
    private static void check(boolean ok, String message) { if (!ok) throw new AssertionError(message); }
    public static void main(String[] args) {
        CoverageIndex index = new CoverageIndex();
        Set<String> oracle = new HashSet<>();
        Random random = new Random(872351);
        for (int i = 0; i < 12000; i++) {
            int x = random.nextInt(150) - 75, z = random.nextInt(150) - 75;
            String dim = i % 5 == 0 ? "nether" : "overworld";
            check(index.add(dim, x, z) == oracle.add(dim + ":" + x + ":" + z), "new/duplicate mismatch");
            int x0 = random.nextInt(80) - 70, z0 = random.nextInt(80) - 70;
            int x1 = x0 + random.nextInt(65), z1 = z0 + random.nextInt(65);
            int missing = 0;
            for (int cz = z0; cz <= z1; cz++) for (int cx = x0; cx <= x1; cx++)
                if (!oracle.contains(dim + ":" + cx + ":" + cz)) missing++;
            check(index.missing(dim, x0, z0, x1, z1) == missing, "region oracle mismatch");
            check(index.missing(dim, x0, z0, x1, z1) == missing, "cached oracle mismatch");
            check(index.complete(dim, x0, z0, x1, z1) == (missing == 0), "window oracle mismatch");
        }
        index.clear();
        check(index.missing("end", -10, -10, 10, 10) == 441, "reset failed");
        for (int z = -10; z <= 10; z++) for (int x = -10; x <= 10; x++) index.add("end", x, z);
        check(index.missing("end", -10, -10, 10, 10) == 0, "incremental counter failed");
        check(!index.complete("overworld", -10, -10, 10, 10), "dimension leaked");
        check(index.missing("end", -11, -11, 11, 11) == 88, "expansion failed");
        for (int anchor : new int[]{-1874999, -34, 0, 54, 1874999}) {
            for (int iteration = 0; iteration < 20; iteration++) {
                int min = anchor - 32 - 16 * iteration, max = anchor + 31 + 16 * iteration;
                List<Integer> centers = CoverageIndex.anchoredCenters(min, max, 10, 20, anchor);
                for (int c = min; c <= max; c++) {
                    int cell = c;
                    check(centers.stream().anyMatch(v -> Math.abs(v - cell) <= 10), "grid leaves a gap");
                }
                for (int j = 1; j < centers.size() - 1; j++) check(Math.floorMod(centers.get(j) - anchor, 20) == 0, "interior grid shifted");
                check(new HashSet<>(centers).size() == centers.size(), "duplicate center");
            }
        }
        System.out.println("PASS: 12,000 differential coverage cases, duplicates, dimensions, reset, negative coordinates, expansion, and anchored-grid coverage.");
    }
}
