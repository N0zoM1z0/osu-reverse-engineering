namespace OsuReverseEngineering.Catch;

/// <summary>
/// Bit-for-bit port of osu!stable's osu_common.Helpers.FastRandom.
/// Catch conversion shares one instance seeded with 1337.
/// </summary>
public sealed class LegacyFastRandom
{
    private uint x;
    private uint y;
    private uint z;
    private uint w;

    public LegacyFastRandom(int seed) => Reinitialise(seed);

    public void Reinitialise(int seed)
    {
        x = (uint)seed;
        y = 842502087u;
        z = 3579807591u;
        w = 273326509u;
    }

    public uint NextUInt()
    {
        var temporary = x ^ (x << 11);
        x = y;
        y = z;
        z = w;
        return w = w ^ (w >> 19) ^ (temporary ^ (temporary >> 8));
    }

    public int Next(int lowerBound, int upperBound)
    {
        if (lowerBound > upperBound)
            throw new ArgumentOutOfRangeException(nameof(lowerBound));

        var range = upperBound - lowerBound;
        var value = NextUInt();
        if (range < 0)
        {
            return lowerBound + (int)(2.3283064365386963E-10
                * value
                * ((long)upperBound - lowerBound));
        }
        return lowerBound + (int)(4.656612873077393E-10
            * (int)(0x7FFFFFFF & value)
            * range);
    }

    public double NextDouble() =>
        4.656612873077393E-10 * (int)(0x7FFFFFFF & NextUInt());
}
