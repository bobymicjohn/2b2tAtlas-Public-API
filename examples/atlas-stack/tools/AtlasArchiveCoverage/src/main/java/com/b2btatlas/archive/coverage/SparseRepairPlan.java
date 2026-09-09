package com.b2btatlas.archive.coverage;

import java.util.*;

/** Route centers cover only requested holes; distant holes never imply a rectangle. */
final class SparseRepairPlan {
    record Cell(int x, int z) {}
    static List<Cell> centers(Collection<Cell> targets, int radius) {
        if (radius < 1 || radius > 32) throw new IllegalArgumentException("Invalid repair radius");
        Map<Cell, List<Cell>> buckets = new HashMap<>();
        Set<Cell> remaining = new HashSet<>(targets);
        for (Cell cell : remaining) buckets.computeIfAbsent(bucket(cell, radius), k -> new ArrayList<>()).add(cell);
        List<Cell> ordered = remaining.stream().sorted(Comparator.comparingInt(Cell::z).thenComparingInt(Cell::x)).toList();
        List<Cell> centers = new ArrayList<>();
        for (Cell center : ordered) {
            if (!remaining.remove(center)) continue;
            centers.add(center);
            Cell grid = bucket(center, radius);
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) {
                for (Cell candidate : buckets.getOrDefault(new Cell(grid.x() + dx, grid.z() + dz), List.of())) {
                    long x = (long)candidate.x() - center.x(), z = (long)candidate.z() - center.z();
                    if (Math.abs(x) <= radius && Math.abs(z) <= radius && x * x + z * z <= (long)radius * radius) remaining.remove(candidate);
                }
            }
        }
        return centers;
    }
    private static Cell bucket(Cell cell, int radius) {
        return new Cell(Math.floorDiv(cell.x(), radius), Math.floorDiv(cell.z(), radius));
    }
}
