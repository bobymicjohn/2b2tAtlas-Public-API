package com.b2btatlas.archive.coverage;

import java.util.*;

/** Sparse, per-dimension region bitsets. No world-sized bitmap or string per lookup. */
final class CoverageIndex {
    private final Map<String, Map<Long, BitSet>> dimensions = new HashMap<>();
    private String cachedDimension;
    private int minX, minZ, maxX, maxZ, cachedMissing;
    void clear() { dimensions.clear(); cachedDimension = null; }
    private static long key(int x, int z) { return ((long)x << 32) ^ (z & 0xffffffffL); }
    boolean add(String dimension, int x, int z) {
        BitSet bits = dimensions.computeIfAbsent(dimension, d -> new HashMap<>())
            .computeIfAbsent(key(x >> 5, z >> 5), k -> new BitSet(1024));
        int bit = ((z & 31) << 5) | (x & 31);
        if (bits.get(bit)) return false;
        bits.set(bit);
        if (dimension.equals(cachedDimension) && x >= minX && x <= maxX && z >= minZ && z <= maxZ) cachedMissing--;
        return true;
    }
    int missing(String dimension, int x0, int z0, int x1, int z1) {
        if (dimension.equals(cachedDimension) && x0 == minX && z0 == minZ && x1 == maxX && z1 == maxZ) return cachedMissing;
        int result = countMissing(dimension, x0, z0, x1, z1);
        cachedDimension = dimension; minX = x0; minZ = z0; maxX = x1; maxZ = z1; cachedMissing = result;
        return result;
    }
    // Local window queries must not evict the incremental whole-survey counter.
    boolean complete(String dimension, int x0, int z0, int x1, int z1) {
        return countMissing(dimension, x0, z0, x1, z1) == 0;
    }
    private int countMissing(String dimension, int x0, int z0, int x1, int z1) {
        if (x1 < x0 || z1 < z0) return 0;
        long present = 0;
        Map<Long, BitSet> regions = dimensions.getOrDefault(dimension, Map.of());
        for (var entry : regions.entrySet()) {
            long packed = entry.getKey();
            long rx = (long)(int)(packed >> 32) * 32, rz = (long)(int)packed * 32;
            if (rx > x1 || rx + 31 < x0 || rz > z1 || rz + 31 < z0) continue;
            int left = (int)Math.max(0, x0 - rx), right = (int)Math.min(31, x1 - rx);
            int top = (int)Math.max(0, z0 - rz), bottom = (int)Math.min(31, z1 - rz);
            BitSet bits = entry.getValue();
            if (left == 0 && right == 31 && top == 0 && bottom == 31) present += bits.cardinality();
            else for (int row = top; row <= bottom; row++) present += bits.get(row * 32 + left, row * 32 + right + 1).cardinality();
        }
        return Math.toIntExact(((long)x1 - x0 + 1) * ((long)z1 - z0 + 1) - present);
    }
    static List<Integer> anchoredCenters(int min, int max, int radius, int step, int anchor) {
        int first = min + radius, last = max - radius;
        if (first >= last) return List.of((int)Math.floorDiv((long)min + max, 2));
        List<Integer> result = new ArrayList<>();
        result.add(first);
        long next = anchor + (Math.floorDiv((long)first - anchor, step) + 1) * step;
        for (; next < last; next += step) result.add((int)next);
        if (result.get(result.size() - 1) != last) result.add(last);
        return result;
    }
}
