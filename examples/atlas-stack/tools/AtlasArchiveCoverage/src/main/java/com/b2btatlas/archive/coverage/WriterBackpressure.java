package com.b2btatlas.archive.coverage;

import java.lang.reflect.Field;
import java.util.Queue;

/** Pace terrain requests without blocking Minecraft's tick or the WDL writer. */
final class WriterBackpressure {
    private Field controllerField, sessionField, writerField, queueField;
    private boolean initialized, absent, paused;
    private long nextSample;
    int pending;
    String failure;

    boolean sample(long now) {
        if (now < nextSample) return paused;
        nextSample = now + 1000;
        try {
            if (!initialized) {
                try {
                    controllerField = field(Class.forName("world.thearchive.wdl.Wdl"), "controller");
                    sessionField = field(Class.forName("world.thearchive.wdl.core.CaptureController"), "session");
                    writerField = field(Class.forName("world.thearchive.wdl.adapter.LiveCaptureSession"), "writer");
                    queueField = field(Class.forName("world.thearchive.wdl.adapter.AsyncSaveWriter"), "queue");
                } catch (ClassNotFoundException ex) {
                    // Standalone coverage runs have no downloader to pace. An
                    // installed but incompatible downloader must fail closed.
                    if (controllerField != null) throw ex;
                    absent = true;
                }
                initialized = true;
            }
            if (absent) return false;
            Object session = sessionField.get(controllerField.get(null));
            Object writer = session == null ? null : writerField.get(session);
            pending = writer == null ? 0 : ((Queue<?>) queueField.get(writer)).size();
            Runtime runtime = Runtime.getRuntime();
            double used = (double) (runtime.totalMemory() - runtime.freeMemory()) / runtime.maxMemory();
            paused = shouldPause(paused, pending, used);
            failure = null;
        } catch (ReflectiveOperationException | RuntimeException ex) {
            failure = ex.getClass().getSimpleName();
            paused = true;
        }
        return paused;
    }

    static boolean shouldPause(boolean paused, int pending, double heapFraction) {
        return paused ? pending > 256 || heapFraction > 0.75 : pending >= 1024 || heapFraction >= 0.85;
    }

    private static Field field(Class<?> type, String name) throws ReflectiveOperationException {
        Field result = type.getDeclaredField(name);
        result.setAccessible(true);
        return result;
    }
}
