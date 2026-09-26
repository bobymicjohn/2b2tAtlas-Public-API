static class Coordinates
{
    public static (long X, long Z) OverworldToNether(long x, long z) =>
        ((long)Math.Floor(x / 8d), (long)Math.Floor(z / 8d));
}
