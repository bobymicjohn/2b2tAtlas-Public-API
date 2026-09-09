using System.IO.Compression;

namespace Atlas.Ingestor.Tests;

internal static class ZipFixture
{
    public static string Create(TempDirectory temporary, params (string Name, byte[] Content)[] entries)
    {
        var path = temporary.Resolve("input.zip");
        using var output = File.Create(path);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
            using var stream = entry.Open();
            stream.Write(content);
        }
        return path;
    }

    public static byte[] MinimalLevelDat(
        string levelName = "Test World",
        int dataVersion = 3955,
        long lastPlayedUnixMilliseconds = 1_754_179_200_000,
        string? playerDimension = null,
        double playerX = 0,
        double playerY = 64,
        double playerZ = 0)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)10);
            WriteString(writer, string.Empty);
            writer.Write((byte)10);
            WriteString(writer, "Data");
            writer.Write((byte)8);
            WriteString(writer, "LevelName");
            WriteString(writer, levelName);
            writer.Write((byte)3);
            WriteString(writer, "DataVersion");
            WriteInt32BigEndian(writer, dataVersion);
            writer.Write((byte)4);
            WriteString(writer, "LastPlayed");
            WriteInt64BigEndian(writer, lastPlayedUnixMilliseconds);
            writer.Write((byte)10);
            WriteString(writer, "Version");
            writer.Write((byte)8);
            WriteString(writer, "Name");
            WriteString(writer, "1.21.1");
            writer.Write((byte)0);
            if (playerDimension is not null)
            {
                writer.Write((byte)10);
                WriteString(writer, "Player");
                writer.Write((byte)8);
                WriteString(writer, "Dimension");
                WriteString(writer, playerDimension);
                writer.Write((byte)9);
                WriteString(writer, "Pos");
                writer.Write((byte)6);
                WriteInt32BigEndian(writer, 3);
                WriteDoubleBigEndian(writer, playerX);
                WriteDoubleBigEndian(writer, playerY);
                WriteDoubleBigEndian(writer, playerZ);
                writer.Write((byte)0);
            }
            writer.Write((byte)0);
            writer.Write((byte)0);
        }
        return output.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        writer.Write(System.Net.IPAddress.HostToNetworkOrder((short)bytes.Length));
        writer.Write(bytes);
    }

    private static void WriteInt32BigEndian(BinaryWriter writer, int value) =>
        writer.Write(System.Net.IPAddress.HostToNetworkOrder(value));

    private static void WriteInt64BigEndian(BinaryWriter writer, long value) =>
        writer.Write(System.Net.IPAddress.HostToNetworkOrder(value));

    private static void WriteDoubleBigEndian(BinaryWriter writer, double value) =>
        WriteInt64BigEndian(writer, BitConverter.DoubleToInt64Bits(value));
}
