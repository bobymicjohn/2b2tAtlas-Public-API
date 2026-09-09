package com.b2btatlas.archive.coverage;

import java.util.*;

public final class SparseRepairPlanTest {
    public static void main(String[] args) {
        Random random = new Random(49211);
        for (int trial=0; trial<1000; trial++) {
            int radius=1+random.nextInt(32);
            Set<SparseRepairPlan.Cell> holes=new HashSet<>();
            for (int i=0;i<300;i++) holes.add(new SparseRepairPlan.Cell(random.nextInt(1000)-500,random.nextInt(1000)-500));
            var centers=SparseRepairPlan.centers(holes,radius);
            if (!holes.containsAll(centers)) throw new AssertionError("Route visited a non-target center");
            for (var hole:holes) {
                boolean covered=centers.stream().anyMatch(c -> Math.hypot((double)c.x()-hole.x(),(double)c.z()-hole.z())<=radius);
                if (!covered) throw new AssertionError("Dropped missing chunk " + hole);
            }
        }
        var far=List.of(new SparseRepairPlan.Cell(-100000000,0),new SparseRepairPlan.Cell(100000000,0));
        if (SparseRepairPlan.centers(far,8).size()!=2) throw new AssertionError("Distant holes changed route");
        if (!SparseRepairPlan.centers(List.of(),8).isEmpty()) throw new AssertionError("Empty route");
        var extreme=List.of(new SparseRepairPlan.Cell(Integer.MIN_VALUE,0),new SparseRepairPlan.Cell(Integer.MAX_VALUE,0));
        if (SparseRepairPlan.centers(extreme,1).size()!=2) throw new AssertionError("Coordinate overflow");
        System.out.println("PASS: 1000 sparse repair coverage trials, negative coordinates, distant holes, integer boundaries.");
    }
}
