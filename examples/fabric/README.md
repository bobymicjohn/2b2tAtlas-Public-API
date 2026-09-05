# Fabric/client-mod integration pattern

This folder is a focused pattern, not a complete mod. [`AtlasApiClient.java`](AtlasApiClient.java) uses Java's standard asynchronous HTTP client plus Gson, which is common in Fabric projects.

## The important threading rule

Never wait for Atlas HTTP on Minecraft's client/render thread.

```java
atlasApi.findLocation("Mu Megabase")
    .thenAccept(location -> MinecraftClient.getInstance().execute(() -> {
        // Safe place to update your mod's client-side waypoint/overlay state.
        overlayState.setSelected(location);
    }))
    .exceptionally(error -> {
        LOGGER.warn("Atlas is unavailable; retaining cached data", error);
        return null;
    });
```

The example client:

- uses `HttpClient.sendAsync`;
- sends a descriptive User-Agent;
- has a bounded request timeout;
- supports location search, group detail, warps, renders, and highways;
- returns `CompletableFuture` so the caller chooses the correct game-thread handoff;
- models unknown/additive JSON fields safely through Gson.

## Recommended mod architecture

```text
AtlasApiClient       HTTP + JSON only
AtlasRepository      TTL/disk cache and background refresh
AtlasSpatialIndex    dimension-aware nearest/bounds queries
AtlasOverlayModel    map-neutral markers and render footprints
JourneyMapAdapter    optional integration
XaeroAdapter         optional integration
AtlasScreen          search/history/source UI
```

Keep map mods optional: isolate their APIs behind adapters and load them only when present. Your domain model should remain usable for a HUD, command, or another map implementation.

## Cache lifecycle

- Load a last-known-good snapshot from the mod config directory.
- Refresh on explicit user action, game startup, or a long TTL—not every tick.
- Build a local dimension/spatial index after parsing.
- If refresh fails, keep the previous snapshot and show a quiet stale indicator.
- Cancel or ignore responses after the user changes server/world context.

## Gson dependency

Use the Gson version supplied by your Minecraft/Fabric toolchain when possible. The example intentionally has no Fabric imports so its HTTP/data layer can be tested outside the game.

## Waypoint safety

Atlas coordinates are public historical records. A mod should describe them as historical landmarks, not active bases or safe destinations. Make bulk import opt-in and let players filter by dimension, era, group, render availability, and distance.
