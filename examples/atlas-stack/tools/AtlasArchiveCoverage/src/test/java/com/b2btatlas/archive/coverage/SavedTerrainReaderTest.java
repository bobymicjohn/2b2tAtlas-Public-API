package com.b2btatlas.archive.coverage;

import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.zip.*;
import net.minecraft.nbt.*;

public final class SavedTerrainReaderTest {
    public static void main(String[] args) throws Exception {
        if (args.length > 0) {
            long start = System.nanoTime(); int[] count = {0};
            SavedTerrainReader.read(Path.of(args[0]), c -> count[0]++);
            System.out.println("SAVED-TERRAIN validated=" + count[0] + " seconds=" + (System.nanoTime() - start) / 1e9);
            return;
        }
        Path fixture = Files.createTempFile("atlas-saved-terrain-", ".zip");
        try {
            write(fixture, false, false);
            List<SavedTerrainReader.Chunk> read = new ArrayList<>();
            SavedTerrainReader.read(fixture, read::add);
            check(read.size() == 2, "Lost saved chunk");
            check(read.get(0).x() == -32 && read.get(1).x() == -31, "Negative region coordinates");
            for (var chunk : read) {
                check(chunk.blocks().get("minecraft:stone") == 4095, "Packed palette count");
                check(chunk.blocks().get("minecraft:beacon") == 1, "Packed final palette entry");
                check(chunk.blockEntities() == 1, "Block entity evidence lost");
            }
            for (int failure = 0; failure < 2; failure++) {
                write(fixture, failure == 0, failure == 1);
                boolean rejected = false;
                try { SavedTerrainReader.read(fixture, c -> {}); } catch (IOException ex) { rejected = true; }
                check(rejected, "Missing external or incorrect coordinate accepted");
            }
            System.out.println("PASS: persisted NBT, negative coordinates, palette counts, block entities, external chunks, corruption rejection.");
        } finally { Files.deleteIfExists(fixture); }
    }
    private static void check(boolean condition, String message) { if (!condition) throw new AssertionError(message); }
    private static byte[] chunk(int x) throws IOException {
        CompoundTag tag = new CompoundTag(); tag.putInt("xPos", x); tag.putInt("zPos", 0);
        ListTag palette = new ListTag();
        for (String name : List.of("minecraft:stone", "minecraft:beacon")) {
            CompoundTag item = new CompoundTag(); item.putString("Name", name); palette.add(item);
        }
        CompoundTag states = new CompoundTag(); states.put("palette", palette);
        long[] data = new long[256]; data[255] = 1L << 60; states.putLongArray("data", data);
        CompoundTag section = new CompoundTag(); section.put("block_states", states);
        ListTag sections = new ListTag(); sections.add(section); tag.put("sections", sections);
        ListTag entities = new ListTag(); entities.add(new CompoundTag()); tag.put("block_entities", entities);
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        try (var compressed = new DeflaterOutputStream(bytes); var output = new DataOutputStream(compressed)) { NbtIo.write(tag, output); }
        return bytes.toByteArray();
    }
    private static void write(Path file, boolean badCoordinate, boolean missingExternal) throws IOException {
        byte[] region = new byte[16384];
        put(region, 0, (2 << 8) | 1); put(region, 4, (3 << 8) | 1);
        byte[] first = chunk(badCoordinate ? 500 : -32);
        put(region, 8192, first.length + 1); region[8196] = 2;
        System.arraycopy(first, 0, region, 8197, first.length);
        put(region, 12288, 1); region[12292] = (byte)130;
        try (ZipOutputStream zip = new ZipOutputStream(Files.newOutputStream(file))) {
            zip.putNextEntry(new ZipEntry("archive-fixture/region/r.-1.0.mca")); zip.write(region); zip.closeEntry();
            if (!missingExternal) {
                zip.putNextEntry(new ZipEntry("archive-fixture/region/c.-31.0.mcc")); zip.write(chunk(-31)); zip.closeEntry();
            }
        }
    }
    private static void put(byte[] bytes, int position, int number) {
        bytes[position] = (byte)(number >>> 24); bytes[position + 1] = (byte)(number >>> 16);
        bytes[position + 2] = (byte)(number >>> 8); bytes[position + 3] = (byte)number;
    }
}
