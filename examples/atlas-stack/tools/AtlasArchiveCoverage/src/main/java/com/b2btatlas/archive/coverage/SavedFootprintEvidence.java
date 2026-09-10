package com.b2btatlas.archive.coverage;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.zip.GZIPOutputStream;

/** Read-only evidence export. Run with the mapped Gradle classpath, never in a live client. */
public final class SavedFootprintEvidence {
    public static void main(String[] args) throws Exception {
        if (args.length != 2 && args.length != 4) throw new IllegalArgumentException("source.zip output.csv.gz [minY maxY]");
        Path source = Path.of(args[0]).toAbsolutePath().normalize();
        Path output = Path.of(args[1]).toAbsolutePath().normalize();
        if (source.equals(output)) throw new IllegalArgumentException("Source must remain immutable");
        int minY = args.length == 4 ? Integer.parseInt(args[2]) : Integer.MIN_VALUE;
        int maxY = args.length == 4 ? Integer.parseInt(args[3]) : Integer.MAX_VALUE;
        if (args.length == 4 && (minY < -2048 || maxY > 2047 || minY > maxY)) throw new IllegalArgumentException("Invalid analysis Y range");
        String sourceHash = sha256(source);
        if (Files.exists(output)) throw new FileAlreadyExistsException(output.toString());
        Path temporary = output.resolveSibling(output.getFileName()+"."+java.util.UUID.randomUUID()+".partial");
        try {
        // Incomplete output is never published as validated evidence.
        try (var writer = new BufferedWriter(new OutputStreamWriter(new GZIPOutputStream(
                Files.newOutputStream(temporary, StandardOpenOption.CREATE_NEW)), StandardCharsets.UTF_8))) {
            writer.write("# source-sha256="+sourceHash+"\n");
            writer.write("# analysis-y="+minY+","+maxY+"\n");
            writer.write("x,z,nonvoid,artificial,strong,block_entities\n");
            long[] total = {0};
            SavedTerrainReader.read(source, chunk -> {
                int[] counts = new int[3];
                chunk.blocks().forEach((name, count) -> BuildEvidence.countNamedBlockEvidence(name, count, counts));
                try {
                    writer.write(chunk.x()+","+chunk.z()+","+(counts[0]>0?1:0)+","+counts[1]+","+counts[2]+","+chunk.blockEntities()+"\n");
                } catch (IOException e) { throw new UncheckedIOException(e); }
                if (++total[0] % 50000 == 0) System.out.println("Inspected " + total[0] + " saved terrain chunks");
            }, minY, maxY);
            System.out.println("Evidence export complete: " + total[0] + " chunks");
        }
        if (!sourceHash.equals(sha256(source))) throw new IOException("Source changed during inspection");
        Files.move(temporary, output);
        } finally { Files.deleteIfExists(temporary); }
    }
    private static String sha256(Path path) throws Exception {
        var digest = java.security.MessageDigest.getInstance("SHA-256");
        try (var input = Files.newInputStream(path)) {
            byte[] buffer = new byte[1024*1024];
            for (int n; (n=input.read(buffer))!=-1;) digest.update(buffer,0,n);
        }
        return java.util.HexFormat.of().formatHex(digest.digest());
    }
}
