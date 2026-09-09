using System.Text;

namespace Xberg.Tests;

/// <summary>
/// Assembles a minimal OLE2 / Compound File Binary container in memory, so tests can build a
/// legacy <c>.doc</c> (or any other CFB-hosted format) from prebuilt streams rather than needing
/// a binary fixture on disk.
/// </summary>
/// <remarks>
/// This is the C# counterpart of upstream's <c>build_doc_ole</c> test helper, which reaches for
/// the <c>cfb</c> crate's writer. It emits the version-3 layout the readers here expect:
/// 512-byte sectors, a DIFAT held entirely in the header, and the mini-stream tier a reader
/// takes for any stream below the 4096-byte cutoff — without which a small stream reads back as
/// zeroes rather than failing outright.
/// </remarks>
internal static class CfbBuilder
{
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const uint MiniStreamCutoff = 4096;
    private const uint Freesect = 0xFFFFFFFF;
    private const uint Endofchain = 0xFFFFFFFE;
    private const uint Fatsect = 0xFFFFFFFD;
    private const uint Nostream = 0xFFFFFFFF;
    private const int DirEntrySize = 128;
    private const int DirEntriesPerSector = SectorSize / DirEntrySize;
    private const int FatEntriesPerSector = SectorSize / 4;

    /// <summary>Build a container holding the given streams at the root storage.</summary>
    /// <param name="streams">Stream name (no leading '/') and its bytes, in directory order.</param>
    public static byte[] Build(params (string Name, byte[] Data)[] streams) =>
        Build(Guid.Empty, streams);

    /// <summary>
    /// Build a container whose root storage declares <paramref name="rootClsid"/> — the class id
    /// that tells one legacy Office binary format from another.
    /// </summary>
    public static byte[] Build(Guid rootClsid, params (string Name, byte[] Data)[] streams)
    {
        // Small streams are packed into the mini stream, which is itself one ordinary
        // sector-chained stream owned by the root entry.
        var miniStream = new List<byte>();
        var startSectors = new uint[streams.Length];
        var isMini = new bool[streams.Length];
        for (int i = 0; i < streams.Length; i++)
        {
            if (streams[i].Data.Length >= MiniStreamCutoff) continue;
            isMini[i] = true;
            startSectors[i] = (uint)(miniStream.Count / MiniSectorSize);
            miniStream.AddRange(streams[i].Data);
            while (miniStream.Count % MiniSectorSize != 0) miniStream.Add(0);
        }
        int miniSectorCount = miniStream.Count / MiniSectorSize;

        // Regular-sector allocation: large streams, then the mini stream, the mini-FAT and the
        // directory. The FAT is placed last because its own size depends on everything above.
        int nextSector = 0;
        var sectorCounts = new int[streams.Length];
        for (int i = 0; i < streams.Length; i++)
        {
            if (isMini[i]) continue;
            sectorCounts[i] = SectorsFor(streams[i].Data.Length);
            startSectors[i] = (uint)nextSector;
            nextSector += sectorCounts[i];
        }

        int miniStreamSectors = SectorsFor(miniStream.Count);
        uint miniStreamStart = miniStream.Count == 0 ? Endofchain : (uint)nextSector;
        if (miniStream.Count > 0) nextSector += miniStreamSectors;

        int miniFatSectors = miniSectorCount == 0
            ? 0
            : (miniSectorCount + FatEntriesPerSector - 1) / FatEntriesPerSector;
        uint miniFatStart = miniFatSectors == 0 ? Endofchain : (uint)nextSector;
        nextSector += miniFatSectors;

        int dirSectorCount = (streams.Length + 1 + DirEntriesPerSector - 1) / DirEntriesPerSector;
        uint dirStart = (uint)nextSector;
        nextSector += dirSectorCount;

        // The FAT must describe itself, so grow it until the count stops moving.
        int fatSectorCount = 1;
        while (true)
        {
            int needed = Math.Max(1,
                (nextSector + fatSectorCount + FatEntriesPerSector - 1) / FatEntriesPerSector);
            if (needed == fatSectorCount) break;
            fatSectorCount = needed;
        }
        if (fatSectorCount > 109)
            throw new InvalidOperationException("streams too large for a header-resident DIFAT");
        uint fatStart = (uint)nextSector;
        int totalSectors = nextSector + fatSectorCount;

        var fat = new uint[fatSectorCount * FatEntriesPerSector];
        Array.Fill(fat, Freesect);
        for (int i = 0; i < streams.Length; i++)
            if (!isMini[i]) Chain(fat, startSectors[i], sectorCounts[i]);
        if (miniStream.Count > 0) Chain(fat, miniStreamStart, miniStreamSectors);
        if (miniFatSectors > 0) Chain(fat, miniFatStart, miniFatSectors);
        Chain(fat, dirStart, dirSectorCount);
        for (int i = 0; i < fatSectorCount; i++) fat[fatStart + i] = Fatsect;

        // The mini-FAT chains mini sectors exactly as the FAT chains sectors.
        var miniFat = new uint[miniFatSectors * FatEntriesPerSector];
        Array.Fill(miniFat, Freesect);
        for (int i = 0; i < streams.Length; i++)
            if (isMini[i] && streams[i].Data.Length > 0)
                Chain(miniFat, startSectors[i], SectorsFor(streams[i].Data.Length, MiniSectorSize));

        var image = new byte[SectorSize * (1 + totalSectors)];
        WriteHeader(image, fatStart, fatSectorCount, dirStart, miniFatStart, miniFatSectors);

        for (int i = 0; i < streams.Length; i++)
            if (!isMini[i]) streams[i].Data.CopyTo(image, SectorOffset(startSectors[i]));
        if (miniStream.Count > 0)
            miniStream.CopyTo(image, SectorOffset(miniStreamStart));
        if (miniFatSectors > 0)
            Buffer.BlockCopy(ToBytes(miniFat), 0, image, SectorOffset(miniFatStart), miniFatSectors * SectorSize);

        WriteDirectory(image, SectorOffset(dirStart), dirSectorCount, streams, startSectors,
            miniStreamStart, miniStream.Count, rootClsid);

        Buffer.BlockCopy(ToBytes(fat), 0, image, SectorOffset(fatStart), fatSectorCount * SectorSize);
        return image;
    }

    private static int SectorsFor(int length, int unit = SectorSize) =>
        Math.Max(1, (length + unit - 1) / unit);

    private static int SectorOffset(uint sector) => SectorSize * (1 + (int)sector);

    private static void Chain(uint[] fat, uint start, int count)
    {
        for (int i = 0; i < count - 1; i++) fat[start + i] = start + (uint)i + 1;
        fat[start + count - 1] = Endofchain;
    }

    private static byte[] ToBytes(uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private static void WriteHeader(
        byte[] image, uint fatStart, int fatSectorCount, uint dirStart,
        uint miniFatStart, int miniFatSectors)
    {
        ReadOnlySpan<byte> signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        signature.CopyTo(image);
        WriteU16(image, 24, 0x003E);       // minor version
        WriteU16(image, 26, 3);            // major version 3 → 512-byte sectors
        WriteU16(image, 28, 0xFFFE);       // little-endian byte order mark
        WriteU16(image, 30, 9);            // sector shift: 1 << 9 == 512
        WriteU16(image, 32, 6);            // mini sector shift: 1 << 6 == 64
        WriteU32(image, 44, (uint)fatSectorCount);
        WriteU32(image, 48, dirStart);
        WriteU32(image, 56, MiniStreamCutoff);
        WriteU32(image, 60, miniFatStart);
        WriteU32(image, 64, (uint)miniFatSectors);
        WriteU32(image, 68, Endofchain);   // no DIFAT beyond the header
        WriteU32(image, 72, 0);

        for (int i = 0; i < 109; i++)
            WriteU32(image, 76 + i * 4, i < fatSectorCount ? fatStart + (uint)i : Freesect);
    }

    private static void WriteDirectory(
        byte[] image, int offset, int dirSectorCount,
        (string Name, byte[] Data)[] streams, uint[] startSectors,
        uint miniStreamStart, int miniStreamLength, Guid rootClsid)
    {
        // Unused entries must read as empty (type 0) with no siblings, rather than as an entry
        // at sector 0 pointing back into the tree.
        for (int i = 0; i < dirSectorCount * DirEntriesPerSector; i++)
        {
            int at = offset + i * DirEntrySize;
            WriteU32(image, at + 68, Nostream);  // left sibling
            WriteU32(image, at + 72, Nostream);  // right sibling
            WriteU32(image, at + 76, Nostream);  // child
        }

        // The root storage owns the mini stream. Its child is the first stream, and the rest
        // hang off that one's right sibling — a degenerate (unbalanced) red-black tree, which
        // readers walk the same way as a balanced one.
        WriteDirEntry(image, offset, "Root Entry", type: 5,
            start: miniStreamStart, size: (ulong)miniStreamLength,
            left: Nostream, right: Nostream, child: streams.Length > 0 ? 1u : Nostream);
        rootClsid.TryWriteBytes(image.AsSpan(offset + 80, 16));

        for (int i = 0; i < streams.Length; i++)
            WriteDirEntry(image, offset + (i + 1) * DirEntrySize, streams[i].Name, type: 2,
                start: startSectors[i], size: (ulong)streams[i].Data.Length,
                left: Nostream,
                right: i + 1 < streams.Length ? (uint)(i + 2) : Nostream,
                child: Nostream);
    }

    private static void WriteDirEntry(
        byte[] image, int at, string name, byte type, uint start, ulong size,
        uint left, uint right, uint child)
    {
        byte[] utf16 = Encoding.Unicode.GetBytes(name);
        utf16.CopyTo(image, at);
        WriteU16(image, at + 64, (ushort)(utf16.Length + 2)); // name length, including the NUL
        image[at + 66] = type;
        image[at + 67] = 1; // colour: black
        WriteU32(image, at + 68, left);
        WriteU32(image, at + 72, right);
        WriteU32(image, at + 76, child);
        WriteU32(image, at + 116, start);
        WriteU64(image, at + 120, size);
    }

    private static void WriteU16(byte[] buf, int offset, ushort value) =>
        BitConverter.TryWriteBytes(buf.AsSpan(offset), value);

    private static void WriteU32(byte[] buf, int offset, uint value) =>
        BitConverter.TryWriteBytes(buf.AsSpan(offset), value);

    private static void WriteU64(byte[] buf, int offset, ulong value) =>
        BitConverter.TryWriteBytes(buf.AsSpan(offset), value);
}
