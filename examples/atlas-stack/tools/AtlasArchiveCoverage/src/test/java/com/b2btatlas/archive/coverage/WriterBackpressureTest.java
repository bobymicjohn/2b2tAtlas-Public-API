package com.b2btatlas.archive.coverage;

public final class WriterBackpressureTest {
    public static void main(String[] args) {
        if (!WriterBackpressure.shouldPause(false, 1024, .5)
                || WriterBackpressure.shouldPause(true, 256, .5)
                || !WriterBackpressure.shouldPause(true, 257, .5)
                || !WriterBackpressure.shouldPause(false, 0, .85)
                || !WriterBackpressure.shouldPause(true, 0, .80)
                || WriterBackpressure.shouldPause(true, 0, .75)) {
            throw new AssertionError("Queue/heap hysteresis failed");
        }
        // The network sends a full terrain window; the disk is far slower.
        // Pausing requests must still drain every accepted chunk, without loss.
        int requested = 0, saved = 0, pending = 0, peak = 0, pauses = 0;
        boolean paused = false;
        for (int tick = 0; tick < 200_000 && saved < 400_000; tick++) {
            paused = WriterBackpressure.shouldPause(paused, pending, .5);
            if (!paused && requested < 400_000) { requested += 400; pending += 400; }
            else if (paused) pauses++;
            peak = Math.max(peak, pending);
            int drain = Math.min(pending, 17);
            pending -= drain;
            saved += drain;
        }
        if (saved != 400_000 || pending != 0 || peak > 1424 || pauses == 0)
            throw new AssertionError("Slow disk simulation lost data or allowed an unbounded queue");
        System.out.println("Writer backpressure: 400000 chunks drained without loss; peak=" + peak);
    }
}
