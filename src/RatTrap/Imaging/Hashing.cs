namespace RatTrap.Imaging;

/// <summary>Small non-cryptographic hashes used to detect that image content changed.</summary>
internal static class Hashing
{
    /// <summary>
    /// FNV-1a 64-bit over a byte span.
    /// </summary>
    /// <remarks>
    /// Used to answer "is this the same pointer shape as last frame?" without allocating a copy
    /// or comparing whole buffers. Collisions are irrelevant here: a false "unchanged" just means
    /// one extra frame is spent before the new shape is noticed.
    /// </remarks>
    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offsetBasis;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
