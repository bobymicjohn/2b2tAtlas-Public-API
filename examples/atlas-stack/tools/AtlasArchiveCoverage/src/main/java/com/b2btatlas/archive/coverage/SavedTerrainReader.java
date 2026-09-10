package com.b2btatlas.archive.coverage;

import java.io.*;
import java.nio.file.Path;
import java.util.*;
import java.util.function.Consumer;
import java.util.regex.Pattern;
import java.util.zip.*;
import net.minecraft.nbt.*;

/** Rebuild coverage evidence from the actual saved terrain, including legacy handoffs. */
final class SavedTerrainReader {
    record Chunk(int x, int z, Map<String, Integer> blocks, int blockEntities) {}
    private static final Pattern REGION = Pattern.compile("^([^/]+/)(?:(DIM-1/|DIM1/))?region/r\\.(-?\\d+)\\.(-?\\d+)\\.mca$");

    static void read(Path path, Consumer<Chunk> consumer) throws IOException {
        read(path, consumer, Integer.MIN_VALUE, Integer.MAX_VALUE);
    }

    /** Height filtering is for offline footprint proposals only. Resume always reads all heights. */
    static void read(Path path, Consumer<Chunk> consumer, int minY, int maxY) throws IOException {
        if (minY > maxY) throw new IllegalArgumentException("Invalid analysis height range");
        boolean filtered = minY != Integer.MIN_VALUE || maxY != Integer.MAX_VALUE;
        try (ZipFile zip = new ZipFile(path.toFile())) {
            String dimensionFolder = null;
            Set<String> coordinates = new HashSet<>();
            var entries = zip.entries();
            while (entries.hasMoreElements()) {
                var entry = entries.nextElement();
                var match = REGION.matcher(entry.getName());
                if (!match.matches()) continue;
                String folder = Objects.toString(match.group(2), "");
                if (dimensionFolder != null && !dimensionFolder.equals(folder)) throw new IOException("Mixed terrain dimensions in handoff");
                dimensionFolder = folder;
                int rx = Integer.parseInt(match.group(3)), rz = Integer.parseInt(match.group(4));
                if (entry.getSize() < 8192 || entry.getSize() > 512L * 1024 * 1024) throw new IOException("Invalid region size");
                byte[] region;
                try (var input = zip.getInputStream(entry)) { region = input.readAllBytes(); }
                for (int slot = 0; slot < 1024; slot++) {
                    int location = integer(region, slot * 4);
                    if (location == 0) continue;
                    int offset = (location >>> 8) * 4096, sectors = location & 255;
                    if (offset < 8192 || sectors == 0 || (long)offset + sectors * 4096L > region.length) throw new IOException("Invalid Anvil location");
                    int length = integer(region, offset), encoding = region[offset + 4] & 255;
                    if (length < 1 || length + 4L > sectors * 4096L) throw new IOException("Invalid Anvil payload length");
                    int x = Math.addExact(Math.multiplyExact(rx, 32), slot % 32);
                    int z = Math.addExact(Math.multiplyExact(rz, 32), slot / 32);
                    InputStream raw;
                    if ((encoding & 128) != 0) {
                        var external = zip.getEntry(match.group(1) + folder + "region/c." + x + "." + z + ".mcc");
                        if (external == null) throw new IOException("Missing external chunk");
                        raw = zip.getInputStream(external);
                    } else raw = new ByteArrayInputStream(region, offset + 5, length - 1);
                    try (InputStream input = switch (encoding & 127) {
                        case 1 -> new GZIPInputStream(raw);
                        case 2 -> new InflaterInputStream(raw);
                        case 3 -> raw;
                        default -> { raw.close(); throw new IOException("Unsupported Anvil compression"); }
                    }) {
                        CompoundTag tag = NbtIo.read(new DataInputStream(input), NbtAccounter.create(32L * 1024 * 1024));
                        if (tag.getInt("xPos").orElseThrow() != x || tag.getInt("zPos").orElseThrow() != z ||
                            !coordinates.add(x + ":" + z)) throw new IOException("Chunk coordinate mismatch or duplicate");
                        Map<String, Integer> counts = new HashMap<>();
                        ListTag sections = tag.getList("sections").orElseThrow(() -> new IOException("Missing modern chunk sections"));
                        for (int i = 0; i < sections.size(); i++) {
                            CompoundTag section = sections.getCompoundOrEmpty(i);
                            int low = 0, high = 15;
                            if (filtered) {
                                int sectionY = section.getByte("Y").orElseThrow(() -> new IOException("Missing section Y")) * 16;
                                low = Math.max(0, minY - sectionY); high = Math.min(15, maxY - sectionY);
                                if (low > high) continue;
                            }
                            CompoundTag states = section.getCompoundOrEmpty("block_states");
                            ListTag palette = states.getListOrEmpty("palette");
                            if (palette.isEmpty()) continue;
                            if (palette.size() > 4096) throw new IOException("Invalid block palette");
                            String[] names = new String[palette.size()];
                            for (int p = 0; p < names.length; p++) names[p] = palette.getCompoundOrEmpty(p).getString("Name").orElseThrow();
                            if (names.length == 1) { counts.merge(names[0], (high-low+1)*256, Integer::sum); continue; }
                            long[] data = states.getLongArray("data").orElseThrow();
                            int bits = Math.max(4, 32 - Integer.numberOfLeadingZeros(names.length - 1));
                            int perLong = 64 / bits;
                            if (data.length != (4096 + perLong - 1) / perLong) throw new IOException("Invalid packed block data");
                            for (int b = low*256; b < (high+1)*256; b++) {
                                int p = (int)((data[b / perLong] >>> ((b % perLong) * bits)) & ((1L << bits) - 1));
                                if (p >= names.length) throw new IOException("Invalid palette index");
                                counts.merge(names[p], 1, Integer::sum);
                            }
                        }
                        ListTag entities = tag.getListOrEmpty("block_entities");
                        int entityCount = entities.size();
                        if (filtered) {
                            entityCount = 0;
                            for (int i = 0; i < entities.size(); i++) {
                                int y = entities.getCompoundOrEmpty(i).getInt("y").orElseThrow(() -> new IOException("Missing block entity y"));
                                if (y >= minY && y <= maxY) entityCount++;
                            }
                        }
                        consumer.accept(new Chunk(x, z, counts, entityCount));
                    }
                }
            }
            if (coordinates.isEmpty()) throw new IOException("Handoff contains no saved terrain");
        }
    }

    private static int integer(byte[] bytes, int offset) {
        return ((bytes[offset] & 255) << 24) | ((bytes[offset + 1] & 255) << 16) |
            ((bytes[offset + 2] & 255) << 8) | (bytes[offset + 3] & 255);
    }
}
