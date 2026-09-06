package example.atlas;

import com.google.gson.Gson;
import com.google.gson.reflect.TypeToken;

import java.io.IOException;
import java.lang.reflect.Type;
import java.net.URI;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;

/** Public read-only 2b2tAtlas client suitable for wrapping in a Fabric mod repository/cache layer. */
public final class AtlasApiClient {
    private static final URI API = URI.create("https://api.blackportal.cloud/");
    private static final Type LOCATIONS = new TypeToken<List<Location>>() {}.getType();
    private static final Type GROUPS = new TypeToken<List<Group>>() {}.getType();
    private static final Type WARPS = new TypeToken<List<Warp>>() {}.getType();
    private static final Type RENDERS = new TypeToken<List<Render>>() {}.getType();
    private static final Type HIGHWAYS = new TypeToken<List<Highway>>() {}.getType();

    private final HttpClient http;
    private final Gson gson;

    public AtlasApiClient() {
        this.http = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(10))
            .followRedirects(HttpClient.Redirect.NORMAL)
            .build();
        this.gson = new Gson();
    }

    public CompletableFuture<List<Location>> locations() {
        return get("api/locations", LOCATIONS);
    }

    public CompletableFuture<Optional<Location>> findLocation(String name) {
        String needle = normalize(name);
        return locations().thenApply(items -> items.stream()
            .filter(item -> normalize(item.name()).equals(needle))
            .findFirst()
            .or(() -> items.stream().filter(item -> normalize(item.name()).contains(needle)).findFirst()));
    }

    public CompletableFuture<List<Group>> groups() {
        return get("api/groups", GROUPS);
    }

    public CompletableFuture<Group> group(int id) {
        return get("api/groups/" + id, Group.class);
    }

    public CompletableFuture<List<Warp>> warpsForLocation(int locationId) {
        return get("api/warps?locationId=" + locationId + "&limit=1000", WARPS);
    }

    public CompletableFuture<List<Render>> rendersForLocation(int locationId) {
        return get("api/renders?locationId=" + locationId + "&limit=1000", RENDERS);
    }

    public CompletableFuture<List<Highway>> highways() {
        return get("api/highways", HIGHWAYS);
    }

    public CompletableFuture<List<Attachment>> imagesForLocation(int locationId) {
        String query = "api/attachments?locationId=" + locationId
            + "&mediaType=" + URLEncoder.encode("Image", StandardCharsets.UTF_8)
            + "&limit=1000";
        Type attachments = new TypeToken<List<Attachment>>() {}.getType();
        return get(query, attachments);
    }

    private <T> CompletableFuture<T> get(String relativePath, Type type) {
        HttpRequest request = HttpRequest.newBuilder(API.resolve(relativePath))
            .timeout(Duration.ofSeconds(30))
            .header("Accept", "application/json")
            .header("User-Agent", "YourFabricMod/1.0 (+https://github.com/you/your-mod)")
            .GET()
            .build();

        return http.sendAsync(request, HttpResponse.BodyHandlers.ofString())
            .thenApply(response -> {
                if (response.statusCode() < 200 || response.statusCode() >= 300) {
                    throw new AtlasApiException("Atlas returned HTTP " + response.statusCode());
                }
                @SuppressWarnings("unchecked")
                T parsed = (T) gson.fromJson(response.body(), type);
                return parsed;
            });
    }

    private <T> CompletableFuture<T> get(String relativePath, Class<T> type) {
        return get(relativePath, (Type) type);
    }

    private static String normalize(String value) {
        return value == null ? "" : value.strip().toLowerCase(Locale.ROOT).replaceAll("\\s+", " ");
    }

    public record Location(
        int rowid,
        String name,
        int dimension,
        String dimensionName,
        long x,
        int y,
        long z,
        String canonicalUrl,
        String interactiveUrl,
        LocationGroup[] groups
    ) {
        public List<LocationGroup> safeGroups() {
            return groups == null ? List.of() : Arrays.asList(groups);
        }
    }

    public record LocationGroup(int groupId, String groupName, String role, String groupApiUrl) {}

    public record Group(
        int id,
        String name,
        String[] aliases,
        String description,
        int locationCount,
        int highwayCount,
        GroupLocation[] locations,
        GroupHighway[] highways,
        String canonicalUrl,
        String interactiveUrl
    ) {}

    public record GroupLocation(
        int locationId, String name, String role, int dimension, long x, long z,
        int renderCount, String locationInteractiveUrl
    ) {}

    public record GroupHighway(
        int highwayId, String name, String role, int dimension, String highwayApiUrl, String mapUrl
    ) {}

    public record Warp(
        int id, Integer locationRowid, String name, String worldDownloadDate,
        String source, String apiUrl
    ) {}

    public record Render(
        int renderId, int locationId, String name, int dimension, String worldDownloadDate,
        String tileUrlTemplate, Long minX, Long minZ, Long maxXExclusive, Long maxZExclusive,
        Integer maxNativeZoom, String coordinateScheme, String apiUrl,
        String worldDownloadUrl, String worldDownloadMetadataUrl, String worldDownloadScope,
        String worldDownloadSha256, String worldDownloadSource,
        String blueMapUrl, String blueMapPath, Integer blueMapProfileVersion
    ) {}

    public record Attachment(
        int id, int locationRowid, String mediaType, String path, String thumbnailPath,
        String sourceUrl, String caption, String attribution, String apiUrl
    ) {}

    public record Highway(
        int id, String name, int dimension, HighwayPoint[] points,
        Integer width, GroupRole[] builderGroups, String apiUrl, String mapUrl
    ) {}

    public record HighwayPoint(long x, long z) {}
    public record GroupRole(int groupId, String groupName, String role, String evidence) {}

    public static final class AtlasApiException extends RuntimeException {
        public AtlasApiException(String message) {
            super(message);
        }
    }
}
