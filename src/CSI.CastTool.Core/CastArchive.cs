using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CSI.CastTool.Core;

public sealed record CastResource(uint Id, uint Offset, uint StoredSize, uint Size, uint Codec, string Tag);

/// <summary>Reader/writer for the inspected little-endian Afterburner FGDC cast layout.</summary>
public sealed class CastArchive
{
    private const int MaximumExpandedBytes = 512 * 1024 * 1024;
    private byte[] Prefix { get; init; } = [];
    private uint MapCodec { get; init; }
    private uint[] Header { get; init; } = [];
    public List<CastResource> Resources { get; } = [];
    public List<uint> InitialOrder { get; } = [];
    public Dictionary<uint, byte[]> Payloads { get; } = [];
    private Dictionary<uint, byte[]> Stored { get; } = [];

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    public static CastArchive Read(byte[] bytes)
    {
        Require(bytes.Length >= 16 && Encoding.ASCII.GetString(bytes, 0, 4) == "XFIR" &&
            Encoding.ASCII.GetString(bytes, 8, 4) == "CDGF", "Only little-endian XFIR/FGDC compressed casts are supported.");
        Require((long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) + 8 == bytes.Length, "Cast size header does not match the file.");
        var reader = new Reader(bytes, 12);
        foreach (string tag in new[] { "Fver", "Fcdr" })
        {
            Require(reader.Tag() == tag, $"Missing {tag} header.");
            reader.Take(reader.Var());
        }
        byte[] prefix = bytes[12..reader.Position];
        Require(reader.Tag() == "ABMP", "Missing ABMP resource map.");
        uint mapLength = reader.Var();
        long mapEnd = (long)reader.Position + mapLength;
        Require(mapEnd <= bytes.Length, "Resource map exceeds file size.");
        uint mapCodec = reader.Var(), expandedMapSize = reader.Var();
        byte[] map = Inflate(reader.Take(checked((uint)(mapEnd - reader.Position))), expandedMapSize);
        var table = new Reader(map);
        uint[] header = [table.Var(), table.Var(), table.Var()];
        Require(header[2] <= 100_000, "Resource count is outside supported limits.");
        var result = new CastArchive { Prefix = prefix, MapCodec = mapCodec, Header = header };
        var ids = new HashSet<uint>();
        for (uint i = 0; i < header[2]; i++)
        {
            var row = new CastResource(table.Var(), table.Var(), table.Var(), table.Var(), table.Var(), table.Tag());
            Require(ids.Add(row.Id), "Duplicate resource ID.");
            result.Resources.Add(row);
        }
        Require(table.Position == map.Length, "Unrecognized resource map trailer.");
        var rows = result.Resources.ToDictionary(x => x.Id);
        Require(rows.ContainsKey(2), "Initial load segment resource is missing.");
        Require(reader.Tag() == "FGEI", "Missing FGEI segment.");
        uint segmentValue = reader.Var();
        int bodyOffset = reader.Position;
        var ilsRow = rows[2];
        byte[] ils = Inflate(reader.Take(ilsRow.StoredSize), ilsRow.Size);
        var segment = new Reader(ils);
        while (segment.Position < ils.Length)
        {
            uint id = segment.Var();
            Require(rows.TryGetValue(id, out var row) && row.Offset == uint.MaxValue, "Invalid initial-segment resource reference.");
            Require(result.Payloads.TryAdd(id, segment.Take(row!.StoredSize)), "Duplicate initial-segment resource.");
            Require(row.StoredSize == row.Size, "Compressed initial-segment members are unsupported.");
            result.InitialOrder.Add(id);
        }
        foreach (var row in result.Resources.Where(x => x.Id != 2 && !result.Payloads.ContainsKey(x.Id)))
        {
            Require(row.Offset != uint.MaxValue, "Initial resource is absent from the load segment.");
            long offset = (long)bodyOffset + row.Offset;
            Require(offset >= bodyOffset && offset + row.StoredSize <= bytes.Length, "Resource exceeds file boundaries.");
            byte[] raw = bytes.AsSpan(checked((int)offset), checked((int)row.StoredSize)).ToArray();
            result.Stored[row.Id] = raw;
            byte[] decoded = raw;
            if (row.StoredSize != row.Size)
            {
                // Opaque JPEG/other codecs are retained verbatim, just as in the tested Python tool.
                try { decoded = Inflate(raw, row.Size); }
                catch (InvalidDataException) { decoded = raw; }
            }
            result.Payloads[row.Id] = decoded;
        }
        result._initialSegmentValue = segmentValue;
        return result;
    }

    private uint _initialSegmentValue;

    public byte[] Rebuild(IReadOnlyDictionary<uint, byte[]> payloads)
    {
        Require(payloads.Keys.Order().SequenceEqual(Payloads.Keys.Order()), "Replacement resource set is incomplete.");
        var rows = Resources.ToDictionary(x => x.Id, x => x);
        foreach (uint id in Payloads.Keys)
        {
            if (payloads[id].AsSpan().SequenceEqual(Payloads[id])) continue;
            var row = rows[id];
            uint length = checked((uint)payloads[id].Length);
            if (row.Offset == uint.MaxValue) rows[id] = row with { Size = length, StoredSize = length };
            else
            {
                byte[] old = Stored[id];
                Require(old.AsSpan().SequenceEqual(Payloads[id]) || IsZlib(old, Payloads[id]), $"Editing compression codec for resource {id} is unsupported.");
                rows[id] = row with { Size = length };
            }
        }
        using var ils = new MemoryStream();
        foreach (uint id in InitialOrder) { WriteVar(ils, id); ils.Write(payloads[id]); }
        byte[] packed = Deflate(ils.ToArray());
        rows[2] = rows[2] with { Offset = 0, Size = checked((uint)ils.Length), StoredSize = checked((uint)packed.Length) };
        using var body = new MemoryStream(); body.Write(packed);
        foreach (var original in Resources.Where(x => x.Id != 2 && x.Offset != uint.MaxValue).OrderBy(x => x.Offset))
        {
            uint id = original.Id;
            byte[] data = payloads[id].AsSpan().SequenceEqual(Payloads[id]) ? Stored[id]
                : Stored[id].AsSpan().SequenceEqual(Payloads[id]) ? payloads[id] : Deflate(payloads[id]);
            rows[id] = rows[id] with { Offset = checked((uint)body.Length), StoredSize = checked((uint)data.Length) };
            body.Write(data);
        }
        using var map = new MemoryStream();
        foreach (uint value in Header) WriteVar(map, value);
        foreach (var original in Resources)
        {
            var row = rows[original.Id];
            foreach (uint value in new[] { row.Id, row.Offset, row.StoredSize, row.Size, row.Codec }) WriteVar(map, value);
            WriteTag(map, row.Tag);
        }
        using var mapSection = new MemoryStream();
        WriteVar(mapSection, MapCodec); WriteVar(mapSection, checked((uint)map.Length)); mapSection.Write(Deflate(map.ToArray()));
        using var file = new MemoryStream();
        file.Write("XFIR"u8); file.Write(new byte[4]); file.Write("CDGF"u8); file.Write(Prefix);
        WriteTag(file, "ABMP"); WriteVar(file, checked((uint)mapSection.Length)); mapSection.Position = 0; mapSection.CopyTo(file);
        WriteTag(file, "FGEI"); WriteVar(file, _initialSegmentValue); body.Position = 0; body.CopyTo(file);
        byte[] output = file.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), checked((uint)output.Length - 8));
        var verified = Read(output);
        Require(verified.InitialOrder.SequenceEqual(InitialOrder), "Repacked initial-segment order verification failed.");
        Require(verified.Resources.Select(x => (x.Id, x.Tag, x.Codec)).SequenceEqual(Resources.Select(x => (x.Id, x.Tag, x.Codec))), "Repacked map verification failed.");
        foreach (var item in payloads) Require(verified.Payloads[item.Key].AsSpan().SequenceEqual(item.Value), $"Repacked resource {item.Key} failed verification.");
        return output;
    }

    private static bool IsZlib(byte[] data, byte[] expected)
    {
        try { return Inflate(data, checked((uint)expected.Length)).AsSpan().SequenceEqual(expected); }
        catch (InvalidDataException) { return false; }
    }
    private static byte[] Inflate(byte[] bytes, uint expected)
    {
        Require(expected <= MaximumExpandedBytes, "Expanded segment is too large.");
        using var input = new MemoryStream(bytes); using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); byte[] buffer = new byte[81920]; int count;
        while ((count = zlib.Read(buffer)) > 0)
        {
            Require(output.Length + count <= expected, "Expanded segment exceeds declared size."); output.Write(buffer, 0, count);
        }
        Require(output.Length == expected, "Expanded size mismatch."); return output.ToArray();
    }
    private static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, true)) zlib.Write(bytes);
        return output.ToArray();
    }
    private static void WriteTag(Stream output, string tag) => output.Write(Encoding.Latin1.GetBytes(new string(tag.Reverse().ToArray())));
    private static void WriteVar(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[5]; int start = 4; bytes[start] = (byte)(value & 127);
        while ((value >>= 7) != 0) bytes[--start] = (byte)((value & 127) | 128);
        output.Write(bytes[start..]);
    }
    private sealed class Reader(byte[] bytes, int position = 0)
    {
        public int Position { get; private set; } = position;
        public byte[] Take(uint length)
        {
            Require((long)Position + length <= bytes.Length, "Unexpected end of cast data.");
            byte[] part = bytes.AsSpan(Position, checked((int)length)).ToArray(); Position += checked((int)length); return part;
        }
        public string Tag() => new(Encoding.Latin1.GetString(Take(4)).Reverse().ToArray());
        public uint Var()
        {
            uint value = 0;
            for (int i = 0; i < 5; i++)
            {
                byte next = Take(1)[0];
                Require(value <= uint.MaxValue >> 7, "Variable integer overflow."); value = (value << 7) | (uint)(next & 127);
                if ((next & 128) == 0) return value;
            }
            throw new InvalidDataException("Invalid variable integer.");
        }
    }
}
