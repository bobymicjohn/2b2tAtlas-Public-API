package com.b2btatlas.archive.coverage;

import static net.fabricmc.fabric.api.client.command.v2.ClientCommandManager.argument;
import static net.fabricmc.fabric.api.client.command.v2.ClientCommandManager.literal;

import com.mojang.brigadier.arguments.IntegerArgumentType;
import com.google.gson.Gson;
import java.util.ArrayDeque;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.loader.api.FabricLoader;
import net.fabricmc.fabric.api.client.command.v2.ClientCommandRegistrationCallback;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientChunkEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.networking.v1.ClientPlayConnectionEvents;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.ResourceKey;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.chunk.LevelChunk;
import net.minecraft.world.level.chunk.LevelChunkSection;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/**
 * Drives bounded and adaptive, auditable routes through an Archive exhibit. Adaptive discovery follows the
 * construction component nearest the warp; it does not claim to recover original-WDL provenance.
 */
public final class AtlasArchiveCoverageClient implements ClientModInitializer {
    private static final Logger LOGGER = LoggerFactory.getLogger("AtlasArchiveCoverage");
    private static final String PREFIX = "ATLAS_COVER";
    private static final CoverageController CONTROLLER = new CoverageController();

    @Override
    public void onInitializeClient() {
        ClientChunkEvents.CHUNK_LOAD.register((level, chunk) ->
            CONTROLLER.onChunkLoaded(level.dimension(), chunk));
        ClientTickEvents.END_CLIENT_TICK.register(CONTROLLER::tick);
        ClientPlayConnectionEvents.DISCONNECT.register((handler, client) -> CONTROLLER.checkpointBeforeDisconnect(client));
        ClientCommandRegistrationCallback.EVENT.register((dispatcher, registryAccess) -> {
            var root = literal("atlascover");
            root.then(literal("status").executes(context -> {
                CONTROLLER.reportStatus(context.getSource().getClient());
                return 1;
            }));
            root.then(literal("reset").executes(context -> {
                CONTROLLER.reset(context.getSource().getClient());
                return 1;
            }));
            root.then(literal("cancel").executes(context -> {
                CONTROLLER.cancel(context.getSource().getClient(), "operator");
                return 1;
            }));
            root.then(literal("checkpoint").executes(context -> {
                CONTROLLER.saveSurveyHint(context.getSource().getClient());
                return 1;
            }));
            root.then(literal("resume").executes(context -> {
                CONTROLLER.resumeSurveyHint(context.getSource().getClient());
                return 1;
            }));
            root.then(literal("repair").executes(context -> {
                CONTROLLER.startSparseRepair(context.getSource().getClient());
                return 1;
            }));
            root.then(literal("adaptive")
                .then(argument("coreRadiusBlocks", IntegerArgumentType.integer(256, 4096))
                .then(argument("expansionBlocks", IntegerArgumentType.integer(128, 2048))
                .then(argument("maxRadiusBlocks", IntegerArgumentType.integer(0, 2_000_000_000))
                .then(argument("terrainRadiusChunks", IntegerArgumentType.integer(2, 32))
                .then(argument("stepChunks", IntegerArgumentType.integer(1, 64))
                    .executes(context -> {
                        CONTROLLER.startAdaptive(
                            context.getSource().getClient(),
                            IntegerArgumentType.getInteger(context, "coreRadiusBlocks"),
                            IntegerArgumentType.getInteger(context, "expansionBlocks"),
                            IntegerArgumentType.getInteger(context, "maxRadiusBlocks"),
                            IntegerArgumentType.getInteger(context, "terrainRadiusChunks"),
                            IntegerArgumentType.getInteger(context, "stepChunks"));
                        return 1;
                    })))))));
            root.then(literal("route")
                .then(argument("minX", IntegerArgumentType.integer())
                .then(argument("minZ", IntegerArgumentType.integer())
                .then(argument("maxXExclusive", IntegerArgumentType.integer())
                .then(argument("maxZExclusive", IntegerArgumentType.integer())
                .then(argument("terrainRadiusChunks", IntegerArgumentType.integer(2, 32))
                .then(argument("stepChunks", IntegerArgumentType.integer(1, 64))
                    .executes(context -> {
                        CONTROLLER.startRoute(
                            context.getSource().getClient(),
                            IntegerArgumentType.getInteger(context, "minX"),
                            IntegerArgumentType.getInteger(context, "minZ"),
                            IntegerArgumentType.getInteger(context, "maxXExclusive"),
                            IntegerArgumentType.getInteger(context, "maxZExclusive"),
                            IntegerArgumentType.getInteger(context, "terrainRadiusChunks"),
                            IntegerArgumentType.getInteger(context, "stepChunks"));
                        return 1;
                    }))))))));
            root.then(literal("teleport")
                .then(argument("minX", IntegerArgumentType.integer())
                .then(argument("minZ", IntegerArgumentType.integer())
                .then(argument("maxXExclusive", IntegerArgumentType.integer())
                .then(argument("maxZExclusive", IntegerArgumentType.integer())
                .then(argument("terrainRadiusChunks", IntegerArgumentType.integer(2, 32))
                .then(argument("stepChunks", IntegerArgumentType.integer(1, 64))
                    .executes(context -> {
                        CONTROLLER.startTeleportRoute(
                            context.getSource().getClient(),
                            IntegerArgumentType.getInteger(context, "minX"),
                            IntegerArgumentType.getInteger(context, "minZ"),
                            IntegerArgumentType.getInteger(context, "maxXExclusive"),
                            IntegerArgumentType.getInteger(context, "maxZExclusive"),
                            IntegerArgumentType.getInteger(context, "terrainRadiusChunks"),
                            IntegerArgumentType.getInteger(context, "stepChunks"));
                        return 1;
                    }))))))));
            dispatcher.register(root);
        });
        String version = FabricLoader.getInstance()
            .getModContainer("atlas_archive_coverage")
            .map(container -> container.getMetadata().getVersion().getFriendlyString())
            .orElse("unknown");
        emit("initialized version=" + version + " baritone=" + BaritoneBridge.isAvailable());
    }

    private static void emit(String message) {
        LOGGER.info("{} {}", PREFIX, message);
    }

    private static void feedback(Minecraft client, String message) {
        emit(message);
        if (client.player != null) {
            client.player.displayClientMessage(Component.literal(PREFIX + " " + message), false);
        }
    }

    private record Waypoint(double x, double z) {}

    private record ChunkCoordinate(int x, int z) {}

    private record ChunkBounds(int minX, int minZ, int maxX, int maxZ) {
        int count() { return (maxX - minX + 1) * (maxZ - minZ + 1); }
    }

    private record BuildComponent(
        Set<ChunkCoordinate> chunks,
        int strongChunks,
        long artificialBlocks,
        long blockEntities
    ) {}

    private record ComponentSelection(
        Set<ChunkCoordinate> chunks,
        String strategy,
        int componentCount,
        int landingBuild,
        int landingStrong,
        int dominantBuild,
        int dominantStrong
    ) {
        static ComponentSelection empty() {
            return new ComponentSelection(Set.of(), "none", 0, 0, 0, 0, 0);
        }
    }

    private record ChunkEvidence(boolean nonVoid, int artificialBlocks, int strongBlocks, int blockEntities) {
        boolean probableBuild() {
            return blockEntities > 0 || strongBlocks >= 2 || artificialBlocks >= 24;
        }

        boolean strongBuild() {
            return blockEntities >= 2 || strongBlocks >= 8 || artificialBlocks >= 96;
        }
    }

    private record EvidenceSummary(
        int received,
        int nonVoid,
        int probableBuild,
        int strongBuild,
        long artificialBlocks,
        long blockEntities,
        int westBuild,
        int eastBuild,
        int northBuild,
        int southBuild,
        int westStrong,
        int eastStrong,
        int northStrong,
        int southStrong
    ) {
        boolean expandWest() { return westStrong > 0 || westBuild >= 2; }
        boolean expandEast() { return eastStrong > 0 || eastBuild >= 2; }
        boolean expandNorth() { return northStrong > 0 || northBuild >= 2; }
        boolean expandSouth() { return southStrong > 0 || southBuild >= 2; }
    }

    private static final class CoverageController {
        private static final double ARRIVAL_TOLERANCE_BLOCKS = 3.0;
        private static final long MIN_SETTLE_MILLIS = 5_000;
        // Missing chunks are still repaired and independently audited after the route.
        // Two quiet seconds proved sufficient for the Archive's bursty local chunk stream
        // while avoiding one unnecessary idle second at nearly every waypoint.
        private static final long LOAD_QUIET_MILLIS = 2_000;
        private static final long MAX_SETTLE_MILLIS = 20_000;
        private static final long WAYPOINT_TIMEOUT_MILLIS = 360_000;
        private static final long TELEPORT_TIMEOUT_MILLIS = 60_000;
        private static final int MAX_REPAIR_PASSES = 3;
        private static final int ADAPTIVE_EDGE_BAND_CHUNKS = 16;
        private static final int ADAPTIVE_COMPONENT_GAP_CHUNKS = 3;
        // Brigadier rejects absolute /tppos destinations outside Minecraft's command
        // coordinate limit even when an Archive warp legitimately lands near or beyond
        // that limit. Do not clamp the survey or claim those chunks were inspected: retain
        // the requested bounds, skip only unreachable route points, and let the missing
        // chunk audit force a low-confidence result for review.
        private static final double TPPOS_MIN_COORDINATE = -29_999_984.0;
        private static final double TPPOS_MAX_COORDINATE = 29_999_984.0;
        // This is deliberately not Minecraft's vanilla +/-30M world border. Archive exhibits
        // can contain out-of-border data. These endpoints only keep chunk-to-block conversion
        // inside the signed 32-bit coordinates used by the collector metadata and /tppos.
        private static final int REPRESENTABLE_MIN_CHUNK = -134_217_727;
        private static final int REPRESENTABLE_MAX_CHUNK = 134_217_726;
        private static final String ADAPTIVE_NONVOID_MANIFEST = "adaptive-nonvoid.csv";

        private final Set<String> receivedChunks = new HashSet<>();
        private final CoverageIndex coverage = new CoverageIndex();
        private final Map<String, ChunkEvidence> chunkEvidence = new HashMap<>();
        private final Set<String> completedAdaptiveWaypoints = new HashSet<>();
        private List<Waypoint> route = List.of();
        private ResourceKey<Level> targetDimension;
        private int waypointIndex;
        private int minChunkX;
        private int minChunkZ;
        private int maxChunkX;
        private int maxChunkZ;
        private int terrainRadiusChunks;
        private int repairPass;
        private long waypointStartedMillis;
        private long arrivedMillis;
        private long lastChunkLoadMillis;
        private double routeStartY;
        private boolean settling;
        private boolean running;
        private boolean useBaritone;
        private boolean pathfinderDispatched;
        private boolean targetConfigured;
        private boolean adaptiveMode;
        private boolean teleportMode;
        private int adaptiveCenterChunkX;
        private int adaptiveCenterChunkZ;
        private int adaptiveCoreRadiusChunks;
        private int adaptiveExpansionChunks;
        private int adaptiveMaxRadiusChunks;
        private int adaptiveStepChunks;
        private int adaptiveIteration;
        private int adaptiveWaypointsCompleted;
        private int boundarySkippedWaypoints;
        private double adaptiveTeleportY;
        private boolean adaptiveMaxRadiusReached;
        private ChunkCoordinate adaptiveComponentAnchor;
        private String adaptiveComponentSelectionStrategy = "none";
        private int adaptiveComponentCount;
        private int adaptiveLandingComponentBuild;
        private int adaptiveLandingComponentStrong;
        private int adaptiveDominantComponentBuild;
        private int adaptiveDominantComponentStrong;
        private long resumeGeneration;
        private String captureId;
        private long lastCheckpointMillis;
        private Set<ChunkCoordinate> sparseTargets;
        private Set<ChunkCoordinate> pendingRepair;

        void onChunkLoaded(ResourceKey<Level> dimension, LevelChunk chunk) {
            int chunkX = chunk.getPos().x;
            int chunkZ = chunk.getPos().z;
            String key = chunkKey(dimension, chunkX, chunkZ);
            receivedChunks.add(key);
            chunkEvidence.put(key, analyzeChunk(chunk));
            coverage.add(dimensionName(dimension), chunkX, chunkZ);
            if (pendingRepair != null && dimension.equals(targetDimension)) pendingRepair.remove(new ChunkCoordinate(chunkX, chunkZ));
            lastChunkLoadMillis = System.currentTimeMillis();
        }

        void reset(Minecraft client) {
            resumeGeneration++;
            captureId = null;
            sparseTargets = null;
            pendingRepair = null;
            releaseKeys(client);
            receivedChunks.clear();
            coverage.clear();
            chunkEvidence.clear();
            completedAdaptiveWaypoints.clear();
            route = List.of();
            targetDimension = client.level == null ? null : client.level.dimension();
            waypointIndex = 0;
            running = false;
            settling = false;
            useBaritone = false;
            pathfinderDispatched = false;
            targetConfigured = false;
            repairPass = 0;
            adaptiveMode = false;
            teleportMode = false;
            adaptiveIteration = 0;
            adaptiveWaypointsCompleted = 0;
            boundarySkippedWaypoints = 0;
            adaptiveMaxRadiusReached = false;
            adaptiveComponentAnchor = null;
            adaptiveComponentSelectionStrategy = "none";
            adaptiveComponentCount = 0;
            adaptiveLandingComponentBuild = 0;
            adaptiveLandingComponentStrong = 0;
            adaptiveDominantComponentBuild = 0;
            adaptiveDominantComponentStrong = 0;
            lastChunkLoadMillis = System.currentTimeMillis();
            feedback(client, "reset dimension=" + dimensionName(targetDimension));
        }

        void cancel(Minecraft client, String reason) {
            resumeGeneration++;
            running = false;
            settling = false;
            adaptiveMode = false;
            teleportMode = false;
            BaritoneBridge.cancel();
            releaseKeys(client);
            feedback(client, "cancelled reason=" + reason + " waypoint=" + waypointIndex + "/" + route.size());
        }

        private record SurveyHint(int schema, String dimension, int centerX, int centerZ,
            int core, int expansion, int maxRadius, int radius, int step,
            int minX, int minZ, int maxX, int maxZ, ChunkCoordinate anchor,
            List<ChunkCoordinate> voidChunks, int iteration, int completedWaypoints, String terrainZip, String captureId) {}

        private record CaptureContext(String captureId) {}

        void checkpointBeforeDisconnect(Minecraft client) {
            if (adaptiveMode && captureId != null) {
                try { persistSurveyHint(client, false, false); }
                catch (Exception ex) { LOGGER.error("ATLAS_COVER checkpoint-failed on disconnect", ex); }
            }
        }

        private Path surveyHintPath() {
            return FabricLoader.getInstance().getConfigDir().resolve("atlas-archive-coverage/survey-hint.json");
        }

        void saveSurveyHint(Minecraft client) {
            if (!running || !adaptiveMode) throw new IllegalStateException("No active adaptive survey to checkpoint.");
            persistSurveyHint(client, true, true);
        }

        private void persistSurveyHint(Minecraft client, boolean stop, boolean latest) {
            List<ChunkCoordinate> voids = new ArrayList<>();
            String prefix = dimensionName(targetDimension) + ":";
            chunkEvidence.forEach((key, evidence) -> {
                if (key.startsWith(prefix) && !evidence.nonVoid()) {
                    String[] xy = key.substring(prefix.length()).split(":");
                    voids.add(new ChunkCoordinate(Integer.parseInt(xy[0]), Integer.parseInt(xy[1])));
                }
            });
            SurveyHint hint = new SurveyHint(3, dimensionName(targetDimension), adaptiveCenterChunkX, adaptiveCenterChunkZ,
                adaptiveCoreRadiusChunks * 16, adaptiveExpansionChunks * 16, adaptiveMaxRadiusChunks * 16,
                terrainRadiusChunks, adaptiveStepChunks, minChunkX, minChunkZ, maxChunkX, maxChunkZ, adaptiveComponentAnchor,
                voids, adaptiveIteration, adaptiveWaypointsCompleted, null, captureId);
            try {
                Path file = latest ? surveyHintPath() : surveyHintPath().getParent().resolve("checkpoints/" + captureId + ".json");
                Path temp = file.resolveSibling(file.getFileName() + ".tmp");
                Files.createDirectories(file.getParent());
                Files.writeString(temp, new Gson().toJson(hint), StandardCharsets.UTF_8);
                try (var channel = java.nio.channels.FileChannel.open(temp, java.nio.file.StandardOpenOption.WRITE)) { channel.force(true); }
                try { Files.move(temp, file, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING); }
                catch (AtomicMoveNotSupportedException ex) { Files.move(temp, file, StandardCopyOption.REPLACE_EXISTING); }
            } catch (java.io.IOException ex) { throw new IllegalStateException("Could not persist survey hint", ex); }
            lastCheckpointMillis = System.currentTimeMillis();
            if (stop) { running = false; releaseKeys(client); }
            if (latest) feedback(client, "checkpoint-saved schema=3 bounds=" + minChunkX + "," + minChunkZ + ".." + maxChunkX + "," + maxChunkZ + " voidChunks=" + voids.size());
        }

        void resumeSurveyHint(Minecraft client) {
            try {
                Path file = surveyHintPath();
                if (Files.size(file) > 256L * 1024 * 1024) throw new IllegalArgumentException("Survey checkpoint is oversized.");
                SurveyHint hint = new Gson().fromJson(Files.readString(file, StandardCharsets.UTF_8), SurveyHint.class);
                LocalPlayer player = requirePlayer(client);
                if ((hint.schema() < 1 || hint.schema() > 3) || !hint.dimension().equals(dimensionName(player.level().dimension())) ||
                    hint.centerX() != player.chunkPosition().x || hint.centerZ() != player.chunkPosition().z)
                    throw new IllegalArgumentException("Survey hint does not match this landing/dimension.");
                requireRepresentableChunk(hint.minX(), hint.minZ());
                requireRepresentableChunk(hint.maxX(), hint.maxZ());
                if (hint.minX() > hint.centerX() || hint.maxX() < hint.centerX() || hint.minZ() > hint.centerZ() || hint.maxZ() < hint.centerZ() ||
                    ((long)hint.maxX() - hint.minX() + 1) * ((long)hint.maxZ() - hint.minZ() + 1) > Integer.MAX_VALUE ||
                    hint.core() < 256 || hint.core() > 4096 || hint.expansion() < 128 || hint.expansion() > 2048 ||
                    hint.radius() < 2 || hint.radius() > 32 || hint.step() < 1 || hint.step() > 64 || hint.maxRadius() != 0)
                    throw new IllegalArgumentException("Invalid/unbounded-only survey hint.");
                Path terrain = Path.of(hint.terrainZip()).toAbsolutePath().normalize();
                if (!terrain.startsWith(Path.of("D:/AtlasIngest/DeferredCaptures").toAbsolutePath().normalize()))
                    throw new IllegalArgumentException("Resume terrain must be in private D staging.");
                // Inflate and inspect saved NBT off the game thread so large handoffs
                // cannot starve network keepalives. Only commit on the client thread.
                feedback(client, "resume-loading terrain=" + terrain.getFileName());
                long generation = ++resumeGeneration;
                java.util.concurrent.CompletableFuture.supplyAsync(() -> {
                    Map<ChunkCoordinate, ChunkEvidence> saved = new HashMap<>();
                    try {
                        SavedTerrainReader.read(terrain, chunk -> {
                            int[] totals = new int[3];
                            chunk.blocks().forEach((name, count) -> countNamedBlockEvidence(name, count, totals));
                            saved.put(new ChunkCoordinate(chunk.x(), chunk.z()),
                                new ChunkEvidence(totals[0] > 0, totals[1], totals[2], chunk.blockEntities()));
                        });
                        return saved;
                    } catch (Exception ex) { throw new java.util.concurrent.CompletionException(ex); }
                }).whenComplete((saved, error) -> client.execute(() -> {
                    if (generation != resumeGeneration) return;
                    if (error != null) { feedback(client, "resume-failed saved terrain validation: " + error.getMessage()); return; }
                    if (client.level == null || client.player == null ||
                        !hint.dimension().equals(dimensionName(client.level.dimension())) ||
                        hint.centerX() != client.player.chunkPosition().x || hint.centerZ() != client.player.chunkPosition().z) {
                        feedback(client, "resume-failed landing changed while loading"); return;
                    }
                    startAdaptive(client, hint.core(), hint.expansion(), hint.maxRadius(), hint.radius(), hint.step());
                    minChunkX = Math.min(minChunkX, hint.minX()); minChunkZ = Math.min(minChunkZ, hint.minZ());
                    maxChunkX = Math.max(maxChunkX, hint.maxX()); maxChunkZ = Math.max(maxChunkZ, hint.maxZ());
                    if (hint.schema() >= 2 && hint.voidChunks() != null) {
                        for (ChunkCoordinate coordinate : hint.voidChunks()) {
                            requireRepresentableChunk(coordinate.x(), coordinate.z());
                            saved.putIfAbsent(coordinate, new ChunkEvidence(false, 0, 0, 0));
                        }
                    }
                    saved.forEach((coordinate, evidence) -> {
                        String key = chunkKey(targetDimension, coordinate.x(), coordinate.z());
                        // Fresh observations since joining take precedence over old data.
                        chunkEvidence.putIfAbsent(key, evidence);
                        receivedChunks.add(key);
                        coverage.add(dimensionName(targetDimension), coordinate.x(), coordinate.z());
                    });
                    adaptiveIteration = Math.max(0, hint.iteration());
                    adaptiveWaypointsCompleted = Math.max(0, hint.completedWaypoints());
                    if (hint.anchor() != null && hint.anchor().x() >= minChunkX && hint.anchor().x() <= maxChunkX &&
                        hint.anchor().z() >= minChunkZ && hint.anchor().z() <= maxChunkZ) adaptiveComponentAnchor = hint.anchor();
                    route = adaptiveWaypoints();
                    if (captureId != null) persistSurveyHint(client, false, false);
                    feedback(client, "resume-start planningOnly=false restored=" + saved.size() + " waypoints=" + route.size() + " received=" + receivedChunks.size());
                }));
            } catch (java.io.IOException ex) { throw new IllegalStateException("Could not read survey hint", ex); }
        }

        void startAdaptive(
            Minecraft client,
            int coreRadiusBlocks,
            int expansionBlocks,
            int maxRadiusBlocks,
            int terrainRadiusChunks,
            int stepChunks
        ) {
            LocalPlayer player = requirePlayer(client);
            if (!player.getAbilities().mayfly) {
                throw new IllegalStateException("Adaptive scanning requires Archive spectator mode (/gmsp)." );
            }
            if (maxRadiusBlocks != 0 && maxRadiusBlocks < coreRadiusBlocks) {
                throw new IllegalArgumentException("Maximum radius cannot be smaller than the core radius.");
            }
            if (stepChunks > terrainRadiusChunks * 2) {
                throw new IllegalArgumentException("Step cannot exceed the terrain window diameter.");
            }
            if (Math.floorDiv(expansionBlocks, 16) < 1) {
                throw new IllegalArgumentException("Expansion must cover at least one chunk.");
            }
            clearAdaptiveNonVoidManifest();
            sparseTargets = null;
            pendingRepair = null;
            captureId = null;
            Path context = surveyHintPath().getParent().resolve("capture-context.json");
            if (Files.isRegularFile(context)) {
                try {
                    String id = new Gson().fromJson(Files.readString(context), CaptureContext.class).captureId();
                    if (id == null || !id.matches("archive-[A-Za-z0-9._-]+") || id.contains("..")) throw new IllegalArgumentException("Invalid capture context");
                    captureId = id;
                } catch (java.io.IOException ex) { throw new IllegalStateException("Cannot read capture context", ex); }
            }

            targetDimension = player.level().dimension();
            adaptiveCenterChunkX = player.chunkPosition().x;
            adaptiveCenterChunkZ = player.chunkPosition().z;
            requireRepresentableChunk(adaptiveCenterChunkX, adaptiveCenterChunkZ);
            adaptiveCoreRadiusChunks = divideRoundUp(coreRadiusBlocks, 16);
            adaptiveExpansionChunks = divideRoundUp(expansionBlocks, 16);
            adaptiveMaxRadiusChunks = maxRadiusBlocks == 0 ? 0 : divideRoundUp(maxRadiusBlocks, 16);
            adaptiveStepChunks = stepChunks;
            this.terrainRadiusChunks = terrainRadiusChunks;
            adaptiveTeleportY = Math.max(150.0, player.getY());
            minChunkX = clampRepresentableChunk((long) adaptiveCenterChunkX - adaptiveCoreRadiusChunks);
            maxChunkX = clampRepresentableChunk((long) adaptiveCenterChunkX + adaptiveCoreRadiusChunks - 1L);
            minChunkZ = clampRepresentableChunk((long) adaptiveCenterChunkZ - adaptiveCoreRadiusChunks);
            maxChunkZ = clampRepresentableChunk((long) adaptiveCenterChunkZ + adaptiveCoreRadiusChunks - 1L);
            targetConfigured = true;
            adaptiveMode = true;
            teleportMode = true;
            adaptiveIteration = 0;
            adaptiveWaypointsCompleted = 0;
            boundarySkippedWaypoints = 0;
            adaptiveMaxRadiusReached = false;
            adaptiveComponentAnchor = null;
            adaptiveComponentSelectionStrategy = "none";
            adaptiveComponentCount = 0;
            adaptiveLandingComponentBuild = 0;
            adaptiveLandingComponentStrong = 0;
            adaptiveDominantComponentBuild = 0;
            adaptiveDominantComponentStrong = 0;
            repairPass = 0;
            completedAdaptiveWaypoints.clear();
            route = adaptiveWaypoints();
            waypointIndex = 0;
            waypointStartedMillis = System.currentTimeMillis();
            lastChunkLoadMillis = waypointStartedMillis;
            settling = false;
            useBaritone = false;
            pathfinderDispatched = false;
            running = true;
            if (captureId != null) persistSurveyHint(client, false, false);
            feedback(client, String.format(Locale.ROOT,
                "adaptive-start dimension=%s center=%d,%d bounds=%d,%d..%d,%d coreRadius=%d " +
                    "expansion=%d maxRadius=%s targetChunks=%d waypoints=%d radius=%d step=%d y=%.2f",
                dimensionName(targetDimension), adaptiveCenterChunkX, adaptiveCenterChunkZ,
                minChunkX, minChunkZ, maxChunkX, maxChunkZ, adaptiveCoreRadiusChunks,
                adaptiveExpansionChunks, adaptiveMaxRadiusChunks == 0 ? "unbounded" : Integer.toString(adaptiveMaxRadiusChunks),
                expectedChunkCount(), route.size(),
                terrainRadiusChunks, adaptiveStepChunks, adaptiveTeleportY));
        }

        void startRoute(
            Minecraft client,
            int minX,
            int minZ,
            int maxXExclusive,
            int maxZExclusive,
            int terrainRadiusChunks,
            int stepChunks
        ) {
            LocalPlayer player = requirePlayer(client);
            if (maxXExclusive <= minX || maxZExclusive <= minZ) {
                throw new IllegalArgumentException("Route bounds must have positive width and height.");
            }
            if (stepChunks > terrainRadiusChunks * 2) {
                throw new IllegalArgumentException("Step cannot exceed the terrain window diameter.");
            }

            sparseTargets = null;
            pendingRepair = null;
            targetDimension = player.level().dimension();
            minChunkX = Math.floorDiv(minX, 16);
            minChunkZ = Math.floorDiv(minZ, 16);
            maxChunkX = Math.floorDiv(maxXExclusive - 1, 16);
            maxChunkZ = Math.floorDiv(maxZExclusive - 1, 16);
            this.terrainRadiusChunks = terrainRadiusChunks;
            targetConfigured = true;
            adaptiveMode = false;
            teleportMode = false;
            repairPass = 0;
            List<Integer> xs = axisCenters(minChunkX, maxChunkX, terrainRadiusChunks, stepChunks);
            List<Integer> zs = axisCenters(minChunkZ, maxChunkZ, terrainRadiusChunks, stepChunks);
            List<Waypoint> generated = new ArrayList<>();
            for (int zIndex = 0; zIndex < zs.size(); zIndex++) {
                if ((zIndex & 1) == 0) {
                    for (int x : xs) generated.add(chunkCenter(x, zs.get(zIndex)));
                } else {
                    for (int xIndex = xs.size() - 1; xIndex >= 0; xIndex--) {
                        generated.add(chunkCenter(xs.get(xIndex), zs.get(zIndex)));
                    }
                }
            }

            route = List.copyOf(generated);
            waypointIndex = 0;
            routeStartY = player.getY();
            waypointStartedMillis = System.currentTimeMillis();
            lastChunkLoadMillis = waypointStartedMillis;
            settling = false;
            // HeadlessMc exposes mayfly/flying state but does not reliably apply synthetic movement keys.
            // Prefer the pinned pathfinder whenever it is installed; direct flight remains a bounded fallback.
            useBaritone = BaritoneBridge.isAvailable();
            if (useBaritone) {
                if (player.getAbilities().flying) {
                    player.getAbilities().flying = false;
                    player.onUpdateAbilities();
                }
                BaritoneBridge.configureNonDestructive();
            } else if (!player.getAbilities().mayfly) {
                throw new IllegalStateException("Walking coverage requires the pinned Baritone pathfinder.");
            }
            pathfinderDispatched = false;
            running = true;
            feedback(client, String.format(Locale.ROOT,
                "route-start dimension=%s bounds=%d,%d..%d,%d targetChunks=%d waypoints=%d radius=%d step=%d " +
                    "x=%.2f y=%.2f z=%.2f mayfly=%s flying=%s pathfinder=%s",
                dimensionName(targetDimension), minChunkX, minChunkZ, maxChunkX, maxChunkZ,
                expectedChunkCount(), route.size(), terrainRadiusChunks, stepChunks,
                player.getX(), player.getY(), player.getZ(), player.getAbilities().mayfly,
                player.getAbilities().flying, useBaritone ? "baritone" : "flight"));
        }

        void startTeleportRoute(
            Minecraft client,
            int minX,
            int minZ,
            int maxXExclusive,
            int maxZExclusive,
            int terrainRadiusChunks,
            int stepChunks
        ) {
            LocalPlayer player = requirePlayer(client);
            if (!player.getAbilities().mayfly) {
                throw new IllegalStateException("Teleport coverage requires Archive spectator mode (/gmsp)." );
            }
            if (maxXExclusive <= minX || maxZExclusive <= minZ) {
                throw new IllegalArgumentException("Teleport bounds must have positive width and height.");
            }
            if (stepChunks > terrainRadiusChunks * 2) {
                throw new IllegalArgumentException("Step cannot exceed the terrain window diameter.");
            }

            sparseTargets = null;
            pendingRepair = null;
            targetDimension = player.level().dimension();
            minChunkX = Math.floorDiv(minX, 16);
            minChunkZ = Math.floorDiv(minZ, 16);
            maxChunkX = Math.floorDiv(maxXExclusive - 1, 16);
            maxChunkZ = Math.floorDiv(maxZExclusive - 1, 16);
            this.terrainRadiusChunks = terrainRadiusChunks;
            adaptiveStepChunks = stepChunks;
            adaptiveTeleportY = Math.max(150.0, player.getY());
            targetConfigured = true;
            adaptiveMode = false;
            teleportMode = true;
            repairPass = 0;
            completedAdaptiveWaypoints.clear();
            route = rectangleWaypoints(false);
            waypointIndex = 0;
            waypointStartedMillis = System.currentTimeMillis();
            lastChunkLoadMillis = waypointStartedMillis;
            settling = false;
            useBaritone = false;
            pathfinderDispatched = false;
            running = true;
            feedback(client, String.format(Locale.ROOT,
                "teleport-route-start dimension=%s bounds=%d,%d..%d,%d targetChunks=%d waypoints=%d radius=%d step=%d y=%.2f",
                dimensionName(targetDimension), minChunkX, minChunkZ, maxChunkX, maxChunkZ,
                expectedChunkCount(), route.size(), terrainRadiusChunks, stepChunks, adaptiveTeleportY));
        }

        private record RepairPlan(int schema, String dimension, int radius, List<ChunkCoordinate> chunks) {}

        void startSparseRepair(Minecraft client) {
            LocalPlayer player = requirePlayer(client);
            if (!player.getAbilities().mayfly) throw new IllegalStateException("Repair requires spectator mode");
            try {
                Path path = surveyHintPath().getParent().resolve("repair-plan.json");
                if (Files.size(path) > 256L * 1024 * 1024) throw new IllegalArgumentException("Oversized repair plan");
                RepairPlan plan = new Gson().fromJson(Files.readString(path), RepairPlan.class);
                if (plan.schema() != 1 || !plan.dimension().equals(dimensionName(player.level().dimension())) ||
                    plan.radius() < 2 || plan.radius() > 32 || plan.chunks() == null || plan.chunks().isEmpty())
                    throw new IllegalArgumentException("Invalid repair plan");
                Set<ChunkCoordinate> targets = new HashSet<>(plan.chunks());
                if (targets.size() != plan.chunks().size()) throw new IllegalArgumentException("Duplicate repair targets");
                for (var target : targets) requireRepresentableChunk(target.x(), target.z());
                reset(client);
                targetDimension = player.level().dimension();
                sparseTargets = targets;
                pendingRepair = new HashSet<>(targets);
                minChunkX = targets.stream().mapToInt(ChunkCoordinate::x).min().orElseThrow();
                maxChunkX = targets.stream().mapToInt(ChunkCoordinate::x).max().orElseThrow();
                minChunkZ = targets.stream().mapToInt(ChunkCoordinate::z).min().orElseThrow();
                maxChunkZ = targets.stream().mapToInt(ChunkCoordinate::z).max().orElseThrow();
                terrainRadiusChunks = plan.radius();
                adaptiveTeleportY = Math.max(150, player.getY());
                targetConfigured = true; teleportMode = true; adaptiveMode = false;
                route = repairWaypoints();
                waypointIndex = 0; repairPass = 0; settling = false; useBaritone = false; pathfinderDispatched = false;
                waypointStartedMillis = lastChunkLoadMillis = System.currentTimeMillis();
                running = true;
                feedback(client, "targeted-repair-start target=" + targets.size() + " waypoints=" + route.size());
            } catch (java.io.IOException ex) { throw new IllegalStateException("Cannot read repair plan", ex); }
        }

        void reportStatus(Minecraft client) {
            LocalPlayer player = client.player;
            if (player == null || client.level == null) {
                feedback(client, "status ingame=false running=" + running + " received=" + receivedChunks.size());
                return;
            }
            var border = client.level.getWorldBorder();
            EvidenceSummary evidence = summarizeEvidence(minChunkX, minChunkZ, maxChunkX, maxChunkZ,
                ADAPTIVE_EDGE_BAND_CHUNKS);
            feedback(client, String.format(Locale.ROOT,
                "status ingame=true running=%s settling=%s dimension=%s x=%.2f y=%.2f z=%.2f chunk=%d,%d " +
                    "mayfly=%s flying=%s borderCenter=%.2f,%.2f borderSize=%.2f received=%d target=%d missing=%d " +
                    "nonvoid=%d built=%d strong=%d waypoint=%d/%d adaptive=%s teleport=%s iteration=%d",
                running, settling, dimensionName(player.level().dimension()), player.getX(), player.getY(), player.getZ(),
                player.chunkPosition().x, player.chunkPosition().z, player.getAbilities().mayfly,
                player.getAbilities().flying, border.getCenterX(), border.getCenterZ(), border.getSize(),
                receivedChunks.size(), expectedChunkCount(), countMissing(), evidence.nonVoid(), evidence.probableBuild(),
                evidence.strongBuild(), waypointIndex, route.size(), adaptiveMode, teleportMode, adaptiveIteration));
        }

        void tick(Minecraft client) {
            if (!running) return;
            LocalPlayer player = client.player;
            if (player == null || client.level == null) {
                checkpointBeforeDisconnect(client);
                cancel(client, "left-world");
                return;
            }
            if (!player.level().dimension().equals(targetDimension)) {
                cancel(client, "dimension-changed");
                return;
            }
            if (waypointIndex >= route.size()) {
                if (adaptiveMode) finishAdaptiveOrExpand(client);
                else if (teleportMode) finishTeleportOrRepair(client);
                else finishOrRepair(client);
                return;
            }

            long now = System.currentTimeMillis();
            if (adaptiveMode && captureId != null && now - lastCheckpointMillis >= 60_000) {
                try { persistSurveyHint(client, false, false); }
                catch (Exception ex) { lastCheckpointMillis = now; LOGGER.error("ATLAS_COVER checkpoint-failed", ex); }
            }
            Waypoint waypoint = route.get(waypointIndex);
            double dx = waypoint.x() - player.getX();
            double dz = waypoint.z() - player.getZ();
            double distanceSquared = dx * dx + dz * dz;
            boolean unreachableTeleport = teleportMode && !canUseTppos(waypoint);

            if (!settling) {
                // Previously loaded AND classified in this capture, including void.
                // Never treat merely visited or unloaded chunks as covered.
                if (adaptiveMode && waypointCovered(waypoint)) {
                    completedAdaptiveWaypoints.add(waypointKey(waypoint));
                    adaptiveWaypointsCompleted++;
                    feedback(client, "coverage-skip waypoint=" + (waypointIndex + 1) + "/" + route.size());
                    waypointIndex++;
                    pathfinderDispatched = false;
                    waypointStartedMillis = now;
                    return;
                }
                if (unreachableTeleport) {
                    releaseKeys(client);
                    feedback(client, "waypoint-skipped waypoint=" + (waypointIndex + 1) + "/" + route.size() +
                        " x=" + (int) Math.floor(waypoint.x()) + " z=" + (int) Math.floor(waypoint.z()) +
                        " reason=tppos-coordinate-limit iteration=" + adaptiveIteration);
                    waypointIndex++;
                    boundarySkippedWaypoints++;
                    if (adaptiveMode) {
                        completedAdaptiveWaypoints.add(waypointKey(waypoint));
                        adaptiveWaypointsCompleted++;
                    }
                    pathfinderDispatched = false;
                    waypointStartedMillis = now;
                    if (waypointIndex >= route.size()) {
                        if (adaptiveMode) finishAdaptiveOrExpand(client);
                        else finishTeleportOrRepair(client);
                    }
                    return;
                }
                long timeout = teleportMode ? TELEPORT_TIMEOUT_MILLIS : WAYPOINT_TIMEOUT_MILLIS;
                if (now - waypointStartedMillis > timeout) {
                    cancel(client, "waypoint-timeout-" + (waypointIndex + 1));
                    return;
                }
                if (distanceSquared <= ARRIVAL_TOLERANCE_BLOCKS * ARRIVAL_TOLERANCE_BLOCKS) {
                    releaseKeys(client);
                    settling = true;
                    arrivedMillis = now;
                    feedback(client, String.format(Locale.ROOT,
                        "arrived waypoint=%d/%d x=%.2f y=%.2f z=%.2f received=%d missing=%d",
                        waypointIndex + 1, route.size(), player.getX(), player.getY(), player.getZ(),
                        receivedChunks.size(), countMissing()));
                    return;
                }
                if (teleportMode) {
                    if (!pathfinderDispatched) {
                        int x = (int) Math.floor(waypoint.x());
                        int z = (int) Math.floor(waypoint.z());
                        player.connection.sendCommand("tppos " + x + " " + (int) Math.ceil(adaptiveTeleportY) + " " + z);
                        pathfinderDispatched = true;
                        feedback(client, "teleport-start waypoint=" + (waypointIndex + 1) + "/" + route.size() +
                            " x=" + x + " z=" + z + " iteration=" + adaptiveIteration);
                    }
                } else if (useBaritone) {
                    if (!pathfinderDispatched) {
                        BaritoneBridge.goTo((int) Math.floor(waypoint.x()), (int) Math.floor(waypoint.z()));
                        pathfinderDispatched = true;
                        feedback(client, "path-start waypoint=" + (waypointIndex + 1) + "/" + route.size() +
                            " x=" + (int) Math.floor(waypoint.x()) + " z=" + (int) Math.floor(waypoint.z()));
                    }
                } else {
                    moveToward(client, player, dx, dz);
                }
                return;
            }

            boolean minimumElapsed = now - arrivedMillis >= MIN_SETTLE_MILLIS;
            boolean quiet = now - lastChunkLoadMillis >= LOAD_QUIET_MILLIS;
            boolean settleTimedOut = now - arrivedMillis >= MAX_SETTLE_MILLIS;
            if (minimumElapsed && (quiet || settleTimedOut || (adaptiveMode && waypointCovered(waypoint)))) {
                feedback(client, "settled waypoint=" + (waypointIndex + 1) + "/" + route.size() +
                    " received=" + receivedChunks.size() + " missing=" + countMissing() + " quiet=" + quiet);
                waypointIndex++;
                if (adaptiveMode) {
                    completedAdaptiveWaypoints.add(waypointKey(waypoint));
                    adaptiveWaypointsCompleted++;
                }
                settling = false;
                pathfinderDispatched = false;
                waypointStartedMillis = now;
                if (repairPass > 0 && countMissing() == 0) {
                    if (adaptiveMode) finishAdaptiveOrExpand(client);
                    else if (teleportMode) finishTeleportOrRepair(client);
                    else finishOrRepair(client);
                    return;
                }
                if (waypointIndex >= route.size()) {
                    if (adaptiveMode) finishAdaptiveOrExpand(client);
                    else if (teleportMode) finishTeleportOrRepair(client);
                    else finishOrRepair(client);
                }
            }
        }

        private static boolean canUseTppos(Waypoint waypoint) {
            return waypoint.x() >= TPPOS_MIN_COORDINATE && waypoint.x() <= TPPOS_MAX_COORDINATE &&
                waypoint.z() >= TPPOS_MIN_COORDINATE && waypoint.z() <= TPPOS_MAX_COORDINATE;
        }

        private void moveToward(Minecraft client, LocalPlayer player, double dx, double dz) {
            if (player.getAbilities().mayfly && !player.getAbilities().flying) {
                player.getAbilities().flying = true;
                player.onUpdateAbilities();
            }
            float yaw = (float) Math.toDegrees(Math.atan2(-dx, dz));
            player.setYRot(yaw);
            player.setXRot(0.0f);
            client.options.keyUp.setDown(true);
            client.options.keyDown.setDown(false);
            client.options.keyLeft.setDown(false);
            client.options.keyRight.setDown(false);
            boolean needsLift = player.getY() < routeStartY - 0.75 || (!player.getAbilities().flying && player.horizontalCollision);
            client.options.keyJump.setDown(needsLift);
            client.options.keyShift.setDown(false);
        }

        private void finishAdaptiveOrExpand(Minecraft client) {
            int missing = countMissing();
            if (missing > 0 && repairPass < MAX_REPAIR_PASSES) {
                List<Waypoint> repair = repairWaypoints();
                if (!repair.isEmpty()) {
                    repairPass++;
                    route = List.copyOf(repair);
                    waypointIndex = 0;
                    waypointStartedMillis = System.currentTimeMillis();
                    settling = false;
                    pathfinderDispatched = false;
                    feedback(client, "adaptive-repair-start pass=" + repairPass + "/" + MAX_REPAIR_PASSES +
                        " missing=" + missing + " repairWaypoints=" + repair.size() +
                        " iteration=" + adaptiveIteration);
                    return;
                }
            }

            Set<ChunkCoordinate> primary = primaryBuildComponent();
            ChunkBounds componentBounds = boundsOf(primary);
            int allowedMinX = adaptiveMaxRadiusChunks == 0
                ? REPRESENTABLE_MIN_CHUNK
                : clampRepresentableChunk((long) adaptiveCenterChunkX - adaptiveMaxRadiusChunks);
            int allowedMaxX = adaptiveMaxRadiusChunks == 0
                ? REPRESENTABLE_MAX_CHUNK
                : clampRepresentableChunk((long) adaptiveCenterChunkX + adaptiveMaxRadiusChunks - 1L);
            int allowedMinZ = adaptiveMaxRadiusChunks == 0
                ? REPRESENTABLE_MIN_CHUNK
                : clampRepresentableChunk((long) adaptiveCenterChunkZ - adaptiveMaxRadiusChunks);
            int allowedMaxZ = adaptiveMaxRadiusChunks == 0
                ? REPRESENTABLE_MAX_CHUNK
                : clampRepresentableChunk((long) adaptiveCenterChunkZ + adaptiveMaxRadiusChunks - 1L);
            int nextMinX = minChunkX;
            int nextMaxX = maxChunkX;
            int nextMinZ = minChunkZ;
            int nextMaxZ = maxChunkZ;

            boolean wantsWest = componentBounds != null &&
                componentBounds.minX() - minChunkX <= adaptiveExpansionChunks;
            boolean wantsEast = componentBounds != null &&
                maxChunkX - componentBounds.maxX() <= adaptiveExpansionChunks;
            boolean wantsNorth = componentBounds != null &&
                componentBounds.minZ() - minChunkZ <= adaptiveExpansionChunks;
            boolean wantsSouth = componentBounds != null &&
                maxChunkZ - componentBounds.maxZ() <= adaptiveExpansionChunks;

            if (wantsWest) nextMinX = Math.max(allowedMinX, clampRepresentableChunk((long) minChunkX - adaptiveExpansionChunks));
            if (wantsEast) nextMaxX = Math.min(allowedMaxX, clampRepresentableChunk((long) maxChunkX + adaptiveExpansionChunks));
            if (wantsNorth) nextMinZ = Math.max(allowedMinZ, clampRepresentableChunk((long) minChunkZ - adaptiveExpansionChunks));
            if (wantsSouth) nextMaxZ = Math.min(allowedMaxZ, clampRepresentableChunk((long) maxChunkZ + adaptiveExpansionChunks));

            boolean requestedBeyondLimit =
                (wantsWest && nextMinX == minChunkX) ||
                (wantsEast && nextMaxX == maxChunkX) ||
                (wantsNorth && nextMinZ == minChunkZ) ||
                (wantsSouth && nextMaxZ == maxChunkZ);
            adaptiveMaxRadiusReached |= requestedBeyondLimit;
            boolean expanded = nextMinX != minChunkX || nextMaxX != maxChunkX ||
                nextMinZ != minChunkZ || nextMaxZ != maxChunkZ;
            if (expanded) {
                minChunkX = nextMinX;
                maxChunkX = nextMaxX;
                minChunkZ = nextMinZ;
                maxChunkZ = nextMaxZ;
                adaptiveIteration++;
                repairPass = 0;
                route = adaptiveWaypoints();
                waypointIndex = 0;
                waypointStartedMillis = System.currentTimeMillis();
                settling = false;
                pathfinderDispatched = false;
                feedback(client, "adaptive-expand iteration=" + adaptiveIteration +
                    " bounds=" + minChunkX + "," + minChunkZ + ".." + maxChunkX + "," + maxChunkZ +
                    " componentBounds=" + formatBounds(componentBounds) +
                    " componentBuild=" + primary.size() +
                    " expand=" + wantsWest + "," + wantsEast + "," + wantsNorth + "," + wantsSouth +
                    " newWaypoints=" + route.size() + " targetChunks=" + expectedChunkCount());
                if (!route.isEmpty()) return;
            }

            int surveyMissing = countMissing();
            ChunkBounds surveyBounds = new ChunkBounds(minChunkX, minChunkZ, maxChunkX, maxChunkZ);
            primary = primaryBuildComponent();
            componentBounds = boundsOf(primary);
            ChunkBounds captureBounds;
            if (componentBounds == null) {
                captureBounds = new ChunkBounds(
                    Math.max(minChunkX, clampRepresentableChunk((long) adaptiveCenterChunkX - adaptiveExpansionChunks)),
                    Math.max(minChunkZ, clampRepresentableChunk((long) adaptiveCenterChunkZ - adaptiveExpansionChunks)),
                    Math.min(maxChunkX, clampRepresentableChunk((long) adaptiveCenterChunkX + adaptiveExpansionChunks - 1L)),
                    Math.min(maxChunkZ, clampRepresentableChunk((long) adaptiveCenterChunkZ + adaptiveExpansionChunks - 1L)));
            } else {
                captureBounds = new ChunkBounds(
                    Math.max(minChunkX, clampRepresentableChunk((long) componentBounds.minX() - adaptiveExpansionChunks)),
                    Math.max(minChunkZ, clampRepresentableChunk((long) componentBounds.minZ() - adaptiveExpansionChunks)),
                    Math.min(maxChunkX, clampRepresentableChunk((long) componentBounds.maxX() + adaptiveExpansionChunks)),
                    Math.min(maxChunkZ, clampRepresentableChunk((long) componentBounds.maxZ() + adaptiveExpansionChunks)));
            }
            int captureMissing = countMissing(captureBounds);
            EvidenceSummary evidence = summarizeEvidence(captureBounds.minX(), captureBounds.minZ(),
                captureBounds.maxX(), captureBounds.maxZ(), ADAPTIVE_EDGE_BAND_CHUNKS);
            EvidenceSummary componentEvidence = summarizeComponent(primary);
            int orphanBuild = Math.max(0, evidence.probableBuild() - primary.size());
            String confidence;
            if (captureMissing > 0 || surveyMissing > 0 || adaptiveMaxRadiusReached || primary.isEmpty()) {
                confidence = "low";
            } else if (componentEvidence.strongBuild() == 0 || primary.size() < 2) confidence = "medium";
            else confidence = "high";
            Path nonVoidManifest = writeAdaptiveNonVoidManifest(captureBounds);
            if (captureId != null) persistSurveyHint(client, false, false);
            running = false;
            settling = false;
            adaptiveMode = false;
            teleportMode = false;
            releaseKeys(client);
            feedback(client, "adaptive-complete confidence=" + confidence +
                " bounds=" + formatBlockBounds(captureBounds) +
                " surveyBounds=" + formatBlockBounds(surveyBounds) +
                " target=" + captureBounds.count() + " missing=" + captureMissing +
                " received=" + evidence.received() + " nonvoid=" + evidence.nonVoid() +
                " built=" + evidence.probableBuild() + " strong=" + evidence.strongBuild() +
                " componentBuild=" + primary.size() + " componentStrong=" + componentEvidence.strongBuild() +
                " orphanBuild=" + orphanBuild +
                " artificialBlocks=" + evidence.artificialBlocks() + " blockEntities=" + evidence.blockEntities() +
                " iterations=" + adaptiveIteration + " waypoints=" + adaptiveWaypointsCompleted +
                " surveyTarget=" + surveyBounds.count() + " surveyMissing=" + surveyMissing +
                " gapChunks=" + ADAPTIVE_COMPONENT_GAP_CHUNKS +
                " marginChunks=" + adaptiveExpansionChunks +
                " maxRadiusReached=" + adaptiveMaxRadiusReached +
                " selection=" + adaptiveComponentSelectionStrategy +
                " components=" + adaptiveComponentCount +
                " landingBuild=" + adaptiveLandingComponentBuild +
                " landingStrong=" + adaptiveLandingComponentStrong +
                " dominantBuild=" + adaptiveDominantComponentBuild +
                " dominantStrong=" + adaptiveDominantComponentStrong +
                " boundarySkipped=" + boundarySkippedWaypoints +
                " nonvoidManifest=" + nonVoidManifest.toString().replace('\\', '/'));
        }

        private static Path adaptiveNonVoidManifestPath() {
            return FabricLoader.getInstance().getConfigDir()
                .resolve("atlas-archive-coverage")
                .resolve(ADAPTIVE_NONVOID_MANIFEST)
                .toAbsolutePath()
                .normalize();
        }

        private static void clearAdaptiveNonVoidManifest() {
            Path path = adaptiveNonVoidManifestPath();
            try {
                Files.deleteIfExists(path);
                Files.deleteIfExists(path.resolveSibling(path.getFileName() + ".tmp"));
            } catch (Exception ex) {
                throw new IllegalStateException("Could not clear the stale adaptive non-void manifest: " + path, ex);
            }
        }

        private Path writeAdaptiveNonVoidManifest(ChunkBounds bounds) {
            Path path = adaptiveNonVoidManifestPath();
            Path temporary = path.resolveSibling(path.getFileName() + ".tmp");
            List<ChunkCoordinate> nonVoid = new ArrayList<>();
            for (int chunkZ = bounds.minZ(); chunkZ <= bounds.maxZ(); chunkZ++) {
                for (int chunkX = bounds.minX(); chunkX <= bounds.maxX(); chunkX++) {
                    ChunkEvidence evidence = chunkEvidence.get(chunkKey(targetDimension, chunkX, chunkZ));
                    if (evidence != null && evidence.nonVoid()) {
                        nonVoid.add(new ChunkCoordinate(chunkX, chunkZ));
                    }
                }
            }
            nonVoid.sort((left, right) -> {
                int byZ = Integer.compare(left.z(), right.z());
                return byZ != 0 ? byZ : Integer.compare(left.x(), right.x());
            });
            List<String> lines = new ArrayList<>(nonVoid.size() + 4);
            lines.add("# atlas-archive-coverage-nonvoid-v1");
            lines.add("# dimension=" + dimensionName(targetDimension));
            lines.add("# boundsChunks=" + formatBounds(bounds));
            lines.add("# count=" + nonVoid.size());
            for (ChunkCoordinate coordinate : nonVoid) {
                lines.add(coordinate.x() + "," + coordinate.z());
            }
            try {
                Files.createDirectories(path.getParent());
                Files.write(temporary, lines, StandardCharsets.UTF_8);
                try {
                    Files.move(temporary, path, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
                } catch (AtomicMoveNotSupportedException ignored) {
                    Files.move(temporary, path, StandardCopyOption.REPLACE_EXISTING);
                }
                return path;
            } catch (Exception ex) {
                try { Files.deleteIfExists(temporary); } catch (Exception ignored) { }
                throw new IllegalStateException("Could not persist the adaptive non-void manifest: " + path, ex);
            }
        }

        private Set<ChunkCoordinate> primaryBuildComponent() {
            Set<ChunkCoordinate> candidates = new HashSet<>();
            for (int chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++) {
                for (int chunkX = minChunkX; chunkX <= maxChunkX; chunkX++) {
                    ChunkEvidence evidence = chunkEvidence.get(chunkKey(targetDimension, chunkX, chunkZ));
                    if (evidence != null && evidence.probableBuild()) {
                        candidates.add(new ChunkCoordinate(chunkX, chunkZ));
                    }
                }
            }
            ComponentSelection selection = selectBuildComponent(candidates);
            adaptiveComponentSelectionStrategy = selection.strategy();
            adaptiveComponentCount = selection.componentCount();
            adaptiveLandingComponentBuild = selection.landingBuild();
            adaptiveLandingComponentStrong = selection.landingStrong();
            adaptiveDominantComponentBuild = selection.dominantBuild();
            adaptiveDominantComponentStrong = selection.dominantStrong();
            return selection.chunks();
        }

        private ComponentSelection selectBuildComponent(Set<ChunkCoordinate> candidates) {
            if (candidates.isEmpty()) return ComponentSelection.empty();

            List<BuildComponent> components = buildComponents(candidates);
            ChunkCoordinate reference = adaptiveComponentAnchor == null
                ? new ChunkCoordinate(adaptiveCenterChunkX, adaptiveCenterChunkZ)
                : adaptiveComponentAnchor;
            BuildComponent landing = nearestComponent(components, reference);
            BuildComponent dominant = dominantComponent(components);
            BuildComponent selected = landing;
            String strategy = adaptiveComponentAnchor == null ? "landing" : "dominant-fallback";

            if (adaptiveComponentAnchor != null) {
                return new ComponentSelection(
                    Set.copyOf(selected.chunks()), strategy, components.size(),
                    adaptiveLandingComponentBuild, adaptiveLandingComponentStrong,
                    selected.chunks().size(), selected.strongChunks());
            }

            // Archive warps occasionally land on a tiny observation platform beside the actual
            // exhibit. Interdimensional Bridge is the canonical example: the old rule selected
            // six landing chunks and discarded a separate 108-chunk bridge. Only switch away
            // from the landing component when it is objectively weak and another component is
            // decisively larger *and* stronger. This keeps ordinary nearby exhibits separate.
            if (adaptiveComponentAnchor == null && dominant != landing &&
                isWeakLandingComponent(landing) && decisivelyDominates(dominant, landing)) {
                selected = dominant;
                strategy = "dominant-fallback";
                adaptiveComponentAnchor = nearestCoordinate(selected.chunks(),
                    new ChunkCoordinate(adaptiveCenterChunkX, adaptiveCenterChunkZ));
            }

            return new ComponentSelection(
                Set.copyOf(selected.chunks()), strategy, components.size(),
                landing.chunks().size(), landing.strongChunks(),
                dominant.chunks().size(), dominant.strongChunks());
        }

        private List<BuildComponent> buildComponents(Set<ChunkCoordinate> source) {
            Set<ChunkCoordinate> remaining = new HashSet<>(source);
            List<BuildComponent> components = new ArrayList<>();
            while (!remaining.isEmpty()) {
                ChunkCoordinate seed = remaining.stream()
                    .min((left, right) -> {
                        int byZ = Integer.compare(left.z(), right.z());
                        return byZ != 0 ? byZ : Integer.compare(left.x(), right.x());
                    })
                    .orElseThrow();
                Set<ChunkCoordinate> chunks = new HashSet<>();
                ArrayDeque<ChunkCoordinate> pending = new ArrayDeque<>();
                remaining.remove(seed);
                chunks.add(seed);
                pending.add(seed);
                while (!pending.isEmpty()) {
                    ChunkCoordinate current = pending.removeFirst();
                    for (int dx = -ADAPTIVE_COMPONENT_GAP_CHUNKS; dx <= ADAPTIVE_COMPONENT_GAP_CHUNKS; dx++) {
                        for (int dz = -ADAPTIVE_COMPONENT_GAP_CHUNKS; dz <= ADAPTIVE_COMPONENT_GAP_CHUNKS; dz++) {
                            ChunkCoordinate neighbor = new ChunkCoordinate(current.x() + dx, current.z() + dz);
                            if (remaining.remove(neighbor)) {
                                chunks.add(neighbor);
                                pending.addLast(neighbor);
                            }
                        }
                    }
                }
                EvidenceSummary evidence = summarizeComponent(chunks);
                components.add(new BuildComponent(Set.copyOf(chunks), evidence.strongBuild(),
                    evidence.artificialBlocks(), evidence.blockEntities()));
            }
            return List.copyOf(components);
        }

        private static BuildComponent nearestComponent(List<BuildComponent> components, ChunkCoordinate reference) {
            return components.stream()
                .min((left, right) -> {
                    int byDistance = Long.compare(
                        minimumDistanceSquared(left.chunks(), reference),
                        minimumDistanceSquared(right.chunks(), reference));
                    if (byDistance != 0) return byDistance;
                    int byStrong = Integer.compare(right.strongChunks(), left.strongChunks());
                    if (byStrong != 0) return byStrong;
                    return Integer.compare(right.chunks().size(), left.chunks().size());
                })
                .orElseThrow();
        }

        private static BuildComponent dominantComponent(List<BuildComponent> components) {
            return components.stream()
                .max((left, right) -> {
                    int byStrong = Integer.compare(left.strongChunks(), right.strongChunks());
                    if (byStrong != 0) return byStrong;
                    int bySize = Integer.compare(left.chunks().size(), right.chunks().size());
                    if (bySize != 0) return bySize;
                    int byEntities = Long.compare(left.blockEntities(), right.blockEntities());
                    if (byEntities != 0) return byEntities;
                    return Long.compare(left.artificialBlocks(), right.artificialBlocks());
                })
                .orElseThrow();
        }

        private static boolean isWeakLandingComponent(BuildComponent landing) {
            return landing.chunks().size() < 8 || landing.strongChunks() < 2;
        }

        private static boolean decisivelyDominates(BuildComponent dominant, BuildComponent landing) {
            int minimumBuild = Math.max(landing.chunks().size() * 3, landing.chunks().size() + 12);
            int minimumStrong = Math.max(landing.strongChunks() * 3, landing.strongChunks() + 8);
            return dominant.chunks().size() >= minimumBuild && dominant.strongChunks() >= minimumStrong;
        }

        private static ChunkCoordinate nearestCoordinate(Set<ChunkCoordinate> coordinates, ChunkCoordinate reference) {
            return coordinates.stream()
                .min((left, right) -> {
                    int byDistance = Long.compare(distanceSquared(left, reference), distanceSquared(right, reference));
                    if (byDistance != 0) return byDistance;
                    int byZ = Integer.compare(left.z(), right.z());
                    return byZ != 0 ? byZ : Integer.compare(left.x(), right.x());
                })
                .orElseThrow();
        }

        private static long minimumDistanceSquared(Set<ChunkCoordinate> coordinates, ChunkCoordinate reference) {
            long best = Long.MAX_VALUE;
            for (ChunkCoordinate coordinate : coordinates) {
                best = Math.min(best, distanceSquared(coordinate, reference));
            }
            return best;
        }

        private static long distanceSquared(ChunkCoordinate coordinate, ChunkCoordinate reference) {
            long dx = (long) coordinate.x() - reference.x();
            long dz = (long) coordinate.z() - reference.z();
            return dx * dx + dz * dz;
        }

        private EvidenceSummary summarizeComponent(Set<ChunkCoordinate> component) {
            int nonVoid = 0;
            int probable = 0;
            int strong = 0;
            long artificial = 0;
            long blockEntities = 0;
            for (ChunkCoordinate coordinate : component) {
                ChunkEvidence evidence = chunkEvidence.get(
                    chunkKey(targetDimension, coordinate.x(), coordinate.z()));
                if (evidence == null) continue;
                if (evidence.nonVoid()) nonVoid++;
                if (evidence.probableBuild()) probable++;
                if (evidence.strongBuild()) strong++;
                artificial += evidence.artificialBlocks();
                blockEntities += evidence.blockEntities();
            }
            return new EvidenceSummary(component.size(), nonVoid, probable, strong, artificial, blockEntities,
                0, 0, 0, 0, 0, 0, 0, 0);
        }

        private static ChunkBounds boundsOf(Set<ChunkCoordinate> coordinates) {
            if (coordinates.isEmpty()) return null;
            int minX = Integer.MAX_VALUE;
            int minZ = Integer.MAX_VALUE;
            int maxX = Integer.MIN_VALUE;
            int maxZ = Integer.MIN_VALUE;
            for (ChunkCoordinate coordinate : coordinates) {
                minX = Math.min(minX, coordinate.x());
                minZ = Math.min(minZ, coordinate.z());
                maxX = Math.max(maxX, coordinate.x());
                maxZ = Math.max(maxZ, coordinate.z());
            }
            return new ChunkBounds(minX, minZ, maxX, maxZ);
        }

        private static String formatBounds(ChunkBounds bounds) {
            return bounds == null ? "none" :
                bounds.minX() + "," + bounds.minZ() + ".." + bounds.maxX() + "," + bounds.maxZ();
        }

        private static String formatBlockBounds(ChunkBounds bounds) {
            return ((long) bounds.minX() * 16L) + "," + ((long) bounds.minZ() * 16L) + ".." +
                (((long) bounds.maxX() + 1L) * 16L) + "," + (((long) bounds.maxZ() + 1L) * 16L);
        }

        private List<Waypoint> adaptiveWaypoints() {
            return rectangleWaypoints(true);
        }

        private List<Waypoint> rectangleWaypoints(boolean omitCompletedAdaptive) {
            List<Integer> xs = omitCompletedAdaptive
                ? CoverageIndex.anchoredCenters(minChunkX, maxChunkX, terrainRadiusChunks, adaptiveStepChunks, adaptiveCenterChunkX)
                : axisCenters(minChunkX, maxChunkX, terrainRadiusChunks, adaptiveStepChunks);
            List<Integer> zs = omitCompletedAdaptive
                ? CoverageIndex.anchoredCenters(minChunkZ, maxChunkZ, terrainRadiusChunks, adaptiveStepChunks, adaptiveCenterChunkZ)
                : axisCenters(minChunkZ, maxChunkZ, terrainRadiusChunks, adaptiveStepChunks);
            List<Waypoint> generated = new ArrayList<>();
            for (int zIndex = 0; zIndex < zs.size(); zIndex++) {
                if ((zIndex & 1) == 0) {
                    for (int x : xs) addRectangleWaypoint(generated, chunkCenter(x, zs.get(zIndex)), omitCompletedAdaptive);
                } else {
                    for (int xIndex = xs.size() - 1; xIndex >= 0; xIndex--) {
                        addRectangleWaypoint(generated, chunkCenter(xs.get(xIndex), zs.get(zIndex)), omitCompletedAdaptive);
                    }
                }
            }
            return List.copyOf(generated);
        }

        private void addRectangleWaypoint(List<Waypoint> generated, Waypoint waypoint, boolean omitCompletedAdaptive) {
            if (!omitCompletedAdaptive || !waypointCovered(waypoint)) {
                generated.add(waypoint);
            }
        }

        private boolean waypointCovered(Waypoint waypoint) {
            int x = Math.floorDiv((int)Math.floor(waypoint.x()), 16);
            int z = Math.floorDiv((int)Math.floor(waypoint.z()), 16);
            // Use a square (stricter than the empirical send circle), clipped only
            // to the requested survey. Unknown cells always remain survey/repair work.
            return coverage.complete(dimensionName(targetDimension),
                Math.max(minChunkX, x - terrainRadiusChunks), Math.max(minChunkZ, z - terrainRadiusChunks),
                Math.min(maxChunkX, x + terrainRadiusChunks), Math.min(maxChunkZ, z + terrainRadiusChunks));
        }

        private EvidenceSummary summarizeEvidence(
            int requestedMinX,
            int requestedMinZ,
            int requestedMaxX,
            int requestedMaxZ,
            int edgeBandChunks
        ) {
            if (targetDimension == null || !targetConfigured) {
                return new EvidenceSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            }
            int received = 0;
            int nonVoid = 0;
            int probableBuild = 0;
            int strongBuild = 0;
            long artificialBlocks = 0;
            long blockEntities = 0;
            int westBuild = 0;
            int eastBuild = 0;
            int northBuild = 0;
            int southBuild = 0;
            int westStrong = 0;
            int eastStrong = 0;
            int northStrong = 0;
            int southStrong = 0;
            for (int chunkZ = requestedMinZ; chunkZ <= requestedMaxZ; chunkZ++) {
                for (int chunkX = requestedMinX; chunkX <= requestedMaxX; chunkX++) {
                    ChunkEvidence evidence = chunkEvidence.get(chunkKey(targetDimension, chunkX, chunkZ));
                    if (evidence == null) continue;
                    received++;
                    if (evidence.nonVoid()) nonVoid++;
                    artificialBlocks += evidence.artificialBlocks();
                    blockEntities += evidence.blockEntities();
                    boolean build = evidence.probableBuild();
                    boolean strong = evidence.strongBuild();
                    if (build) probableBuild++;
                    if (strong) strongBuild++;
                    if (build && chunkX - requestedMinX < edgeBandChunks) westBuild++;
                    if (build && requestedMaxX - chunkX < edgeBandChunks) eastBuild++;
                    if (build && chunkZ - requestedMinZ < edgeBandChunks) northBuild++;
                    if (build && requestedMaxZ - chunkZ < edgeBandChunks) southBuild++;
                    if (strong && chunkX - requestedMinX < edgeBandChunks) westStrong++;
                    if (strong && requestedMaxX - chunkX < edgeBandChunks) eastStrong++;
                    if (strong && chunkZ - requestedMinZ < edgeBandChunks) northStrong++;
                    if (strong && requestedMaxZ - chunkZ < edgeBandChunks) southStrong++;
                }
            }
            return new EvidenceSummary(received, nonVoid, probableBuild, strongBuild, artificialBlocks, blockEntities,
                westBuild, eastBuild, northBuild, southBuild, westStrong, eastStrong, northStrong, southStrong);
        }

        private static ChunkEvidence analyzeChunk(LevelChunk chunk) {
            int[] counts = new int[3];
            for (LevelChunkSection section : chunk.getSections()) {
                if (section == null || section.hasOnlyAir()) continue;
                section.getStates().count((state, count) -> countBlockEvidence(state, count, counts));
            }
            return new ChunkEvidence(counts[0] > 0, counts[1], counts[2], chunk.getBlockEntities().size());
        }

        private static void countBlockEvidence(BlockState state, int count, int[] totals) {
            if (state.isAir()) return;
            String path = BuiltInRegistries.BLOCK.getKey(state.getBlock()).getPath();
            countNamedBlockEvidence(path, count, totals);
        }

        private static void countNamedBlockEvidence(String name, int count, int[] totals) {
            String path = name.startsWith("minecraft:") ? name.substring(10) : name;
            if (path.equals("air") || path.equals("cave_air") || path.equals("void_air")) return;
            totals[0] += count;
            if (isArtificialBlock(path)) totals[1] += count;
            if (isStrongArtificialBlock(path)) totals[2] += count;
        }

        private static boolean isArtificialBlock(String path) {
            return isStrongArtificialBlock(path) ||
                containsAny(path, "planks", "bricks", "glass", "pane", "concrete", "wool", "carpet",
                    "slab", "stairs", "fence", "wall", "door", "trapdoor", "torch", "lantern", "chain",
                    "iron_bars", "ladder", "scaffolding", "chest", "barrel", "furnace", "crafting_table",
                    "bookshelf", "farmland", "polished", "cut_", "chiseled", "glazed_terracotta",
                    "copper_bulb", "copper_grate", "waxed_", "purpur", "quartz_block", "quartz_pillar");
        }

        private static boolean isStrongArtificialBlock(String path) {
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

        private static boolean containsAny(String value, String... needles) {
            for (String needle : needles) {
                if (value.contains(needle)) return true;
            }
            return false;
        }

        private static boolean equalsAny(String value, String... candidates) {
            for (String candidate : candidates) {
                if (value.equals(candidate)) return true;
            }
            return false;
        }

        private static boolean endsWithAny(String value, String... suffixes) {
            for (String suffix : suffixes) {
                if (value.endsWith(suffix)) return true;
            }
            return false;
        }

        private void finishOrRepair(Minecraft client) {
            int missing = countMissing();
            if (missing > 0 && repairPass < MAX_REPAIR_PASSES) {
                List<Waypoint> repair = repairWaypoints();
                if (!repair.isEmpty()) {
                    repairPass++;
                    List<Waypoint> expanded = new ArrayList<>(route);
                    expanded.addAll(repair);
                    route = List.copyOf(expanded);
                    waypointStartedMillis = System.currentTimeMillis();
                    settling = false;
                    pathfinderDispatched = false;
                    feedback(client, "repair-start pass=" + repairPass + "/" + MAX_REPAIR_PASSES +
                        " missing=" + missing + " repairWaypoints=" + repair.size() +
                        " totalWaypoints=" + route.size());
                    return;
                }
            }

            running = false;
            settling = false;
            BaritoneBridge.cancel();
            releaseKeys(client);
            feedback(client, "complete exact=" + (missing == 0) + " target=" + expectedChunkCount() +
                " missing=" + missing + " received=" + receivedChunks.size() + " waypoints=" + route.size() +
                " repairPasses=" + repairPass);
        }

        private void finishTeleportOrRepair(Minecraft client) {
            int missing = countMissing();
            if (missing > 0 && repairPass < MAX_REPAIR_PASSES) {
                List<Waypoint> repair = repairWaypoints();
                if (!repair.isEmpty()) {
                    repairPass++;
                    route = List.copyOf(repair);
                    waypointIndex = 0;
                    waypointStartedMillis = System.currentTimeMillis();
                    settling = false;
                    pathfinderDispatched = false;
                    feedback(client, "teleport-repair-start pass=" + repairPass + "/" + MAX_REPAIR_PASSES +
                        " missing=" + missing + " repairWaypoints=" + repair.size());
                    return;
                }
            }

            running = false;
            settling = false;
            teleportMode = false;
            releaseKeys(client);
            feedback(client, "teleport-complete exact=" + (missing == 0) +
                " target=" + expectedChunkCount() + " missing=" + missing +
                " received=" + receivedChunks.size() + " waypoints=" + route.size() +
                " repairPasses=" + repairPass);
        }

        private List<Waypoint> repairWaypoints() {
            List<ChunkCoordinate> remaining = new ArrayList<>();
            if (pendingRepair != null) remaining.addAll(pendingRepair);
            else for (int chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++) {
                for (int chunkX = minChunkX; chunkX <= maxChunkX; chunkX++) {
                    if (!receivedChunks.contains(chunkKey(targetDimension, chunkX, chunkZ))) {
                        remaining.add(new ChunkCoordinate(chunkX, chunkZ));
                    }
                }
            }

            List<Waypoint> repair = new ArrayList<>();
            // Teleport modes go directly to the repair center, so they can use the full
            // empirically verified server send radius. Bounded walking keeps the smaller
            // grouping because terrain and pathing can stop Baritone short of a waypoint.
            int conservativeRadius = teleportMode
                ? Math.max(1, terrainRadiusChunks)
                : Math.max(1, terrainRadiusChunks / 2);
            var cells = remaining.stream().map(c -> new SparseRepairPlan.Cell(c.x(), c.z())).toList();
            for (var center : SparseRepairPlan.centers(cells, conservativeRadius)) {
                Waypoint waypoint = chunkCenter(center.x(), center.z());
                if (!teleportMode || canUseTppos(waypoint)) repair.add(waypoint);
            }
            return repair;
        }

        private int countMissing() {
            if (pendingRepair != null) return pendingRepair.size();
            if (!targetConfigured || targetDimension == null) return 0;
            return countMissing(new ChunkBounds(minChunkX, minChunkZ, maxChunkX, maxChunkZ));
        }

        private int countMissing(ChunkBounds bounds) {
            if (!targetConfigured || targetDimension == null) return 0;
            return coverage.missing(dimensionName(targetDimension), bounds.minX(), bounds.minZ(), bounds.maxX(), bounds.maxZ());
        }

        private int expectedChunkCount() {
            if (sparseTargets != null) return sparseTargets.size();
            if (!targetConfigured) return 0;
            return (maxChunkX - minChunkX + 1) * (maxChunkZ - minChunkZ + 1);
        }

        private static List<Integer> axisCenters(int min, int max, int radius, int step) {
            int first = min + radius;
            int last = max - radius;
            if (first >= last) return List.of(Math.floorDiv(min + max, 2));
            List<Integer> values = new ArrayList<>();
            values.add(first);
            for (int value = first + step; value < last; value += step) values.add(value);
            if (values.get(values.size() - 1) != last) values.add(last);
            return values;
        }

        private static Waypoint chunkCenter(int chunkX, int chunkZ) {
            return new Waypoint(chunkX * 16.0 + 8.0, chunkZ * 16.0 + 8.0);
        }

        private static String waypointKey(Waypoint waypoint) {
            return ((int) Math.floor(waypoint.x())) + ":" + ((int) Math.floor(waypoint.z()));
        }

        private static int divideRoundUp(int value, int divisor) {
            return Math.floorDiv(value + divisor - 1, divisor);
        }

        private static int clampRepresentableChunk(long chunk) {
            return (int) Math.max(REPRESENTABLE_MIN_CHUNK, Math.min(REPRESENTABLE_MAX_CHUNK, chunk));
        }

        private static void requireRepresentableChunk(int chunkX, int chunkZ) {
            if (chunkX < REPRESENTABLE_MIN_CHUNK || chunkX > REPRESENTABLE_MAX_CHUNK ||
                chunkZ < REPRESENTABLE_MIN_CHUNK || chunkZ > REPRESENTABLE_MAX_CHUNK) {
                throw new IllegalArgumentException(
                    "Warp coordinates exceed the collector's signed block-coordinate representation.");
            }
        }

        private static String chunkKey(ResourceKey<Level> dimension, int chunkX, int chunkZ) {
            return dimension.identifier() + ":" + chunkX + ":" + chunkZ;
        }

        private static String dimensionName(ResourceKey<Level> dimension) {
            return dimension == null ? "none" : dimension.identifier().toString();
        }

        private static LocalPlayer requirePlayer(Minecraft client) {
            if (client.player == null || client.level == null) {
                throw new IllegalStateException("Atlas coverage requires an active world.");
            }
            return client.player;
        }

        private static void releaseKeys(Minecraft client) {
            client.options.keyUp.setDown(false);
            client.options.keyDown.setDown(false);
            client.options.keyLeft.setDown(false);
            client.options.keyRight.setDown(false);
            client.options.keyJump.setDown(false);
            client.options.keyShift.setDown(false);
        }
    }

    /** Keeps Baritone optional at compile time while using its stable public API when the pinned runtime is present. */
    private static final class BaritoneBridge {
        private static final String API_CLASS = "baritone.api.BaritoneAPI";

        static boolean isAvailable() {
            try {
                Class.forName(API_CLASS, false, AtlasArchiveCoverageClient.class.getClassLoader());
                return true;
            } catch (ClassNotFoundException ignored) {
                return false;
            }
        }

        static void configureNonDestructive() {
            try {
                Object settings = Class.forName(API_CLASS).getMethod("getSettings").invoke(null);
                setSetting(settings, "allowBreak", false);
                setSetting(settings, "allowPlace", false);
                setSetting(settings, "allowPlaceInFluidsSource", false);
                setSetting(settings, "allowPlaceInFluidsFlow", false);
                setSetting(settings, "allowParkour", false);
                setSetting(settings, "allowParkourPlace", false);
                setSetting(settings, "allowSprint", true);
            } catch (ReflectiveOperationException exception) {
                throw new IllegalStateException("Could not configure the pinned Baritone API.", exception);
            }
        }

        static void goTo(int x, int z) {
            try {
                Class<?> api = Class.forName(API_CLASS);
                Object provider = api.getMethod("getProvider").invoke(null);
                Object baritone = Class.forName("baritone.api.IBaritoneProvider")
                    .getMethod("getPrimaryBaritone").invoke(provider);
                Object process = Class.forName("baritone.api.IBaritone")
                    .getMethod("getCustomGoalProcess").invoke(baritone);
                Class<?> goalInterface = Class.forName("baritone.api.pathing.goals.Goal");
                Class<?> goalClass = Class.forName("baritone.api.pathing.goals.GoalXZ");
                Constructor<?> constructor = goalClass.getConstructor(int.class, int.class);
                Object goal = constructor.newInstance(x, z);
                Method setGoalAndPath = Class.forName("baritone.api.process.ICustomGoalProcess")
                    .getMethod("setGoalAndPath", goalInterface);
                setGoalAndPath.invoke(process, goal);
            } catch (ReflectiveOperationException exception) {
                throw new IllegalStateException("Could not start the pinned Baritone pathfinder.", exception);
            }
        }

        static void cancel() {
            if (!isAvailable()) return;
            try {
                Class<?> api = Class.forName(API_CLASS);
                Object provider = api.getMethod("getProvider").invoke(null);
                Object baritone = Class.forName("baritone.api.IBaritoneProvider")
                    .getMethod("getPrimaryBaritone").invoke(provider);
                Object behavior = Class.forName("baritone.api.IBaritone")
                    .getMethod("getPathingBehavior").invoke(baritone);
                Class.forName("baritone.api.behavior.IPathingBehavior")
                    .getMethod("cancelEverything").invoke(behavior);
            } catch (ReflectiveOperationException exception) {
                emit("baritone-cancel-warning type=" + exception.getClass().getSimpleName());
            }
        }

        private static void setSetting(Object settings, String name, boolean value) throws ReflectiveOperationException {
            Field settingField = settings.getClass().getField(name);
            Object setting = settingField.get(settings);
            Field valueField = setting.getClass().getField("value");
            valueField.set(setting, value);
        }
    }
}
