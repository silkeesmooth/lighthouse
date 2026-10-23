using System.Buffers.Binary;
using System.Text;

namespace Lighthouse.Core.Rendering;

/// <summary>
/// Joins PNG frames (same size and format) into an animated PNG that loops forever. Unlike a GIF it keeps full colour,
/// so a sky gradient has no bands, and real see-through edges, so rounded corners suit any page behind them.
/// Browsers play it from a plain .png file; anything else shows the first frame.
/// </summary>
public static class Apng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <param name="frames">Each frame as a whole PNG file.</param>
    /// <param name="delayNum">Each frame's time as a fraction of a second: <paramref name="delayNum"/>/<paramref name="delayDen"/>.</param>
    public static byte[] Make(IReadOnlyList<byte[]> frames, ushort delayNum, ushort delayDen)
    {
        if (frames.Count == 0) throw new ArgumentException("no frames", nameof(frames));
        var parsed = frames.Select(Chunks).ToList();
        byte[] header = parsed[0].First(c => c.Type == "IHDR").Data;
        if (parsed.Any(p => !p.First(c => c.Type == "IHDR").Data.AsSpan().SequenceEqual(header)))
            throw new InvalidDataException("frames differ in size or format");
        int width = BinaryPrimitives.ReadInt32BigEndian(header), height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));

        using var output = new MemoryStream();
        output.Write(Signature);
        Write(output, "IHDR", header);
        // The first frame's own colour and size notes (sRGB, pHYs...) come before any image data.
        foreach (var chunk in parsed[0].TakeWhile(c => c.Type != "IDAT").Where(c => c.Type != "IHDR")) Write(output, chunk.Type, chunk.Data);
        var actl = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(actl, frames.Count);
        BinaryPrimitives.WriteInt32BigEndian(actl.AsSpan(4), 0); // plays: 0 = forever
        Write(output, "acTL", actl);

        int sequence = 0;
        for (int f = 0; f < parsed.Count; f++)
        {
            var fctl = new byte[26];
            BinaryPrimitives.WriteInt32BigEndian(fctl, sequence++);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(4), width);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(8), height);
            // x and y offsets stay 0: every frame is the whole picture.
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), delayNum);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), delayDen);
            fctl[24] = 0; // dispose: none
            fctl[25] = 0; // blend: source, so a see-through corner stays see-through
            Write(output, "fcTL", fctl);
            foreach (var data in parsed[f].Where(c => c.Type == "IDAT").Select(c => c.Data))
            {
                if (f == 0)
                {
                    Write(output, "IDAT", data);
                    continue;
                }
                var fdat = new byte[data.Length + 4];
                BinaryPrimitives.WriteInt32BigEndian(fdat, sequence++);
                data.CopyTo(fdat, 4);
                Write(output, "fdAT", fdat);
            }
        }
        Write(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>A PNG file's chunks, in order.</summary>
    public static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a PNG");
        var chunks = new List<(string, byte[])>();
        for (int at = 8; at + 12 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = Encoding.ASCII.GetString(png, at + 4, 4);
            chunks.Add((type, png.AsSpan(at + 8, length).ToArray()));
            at += 12 + length;
            if (type == "IEND") break;
        }
        return chunks;
    }

    private static void Write(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typeBytes, data));
        output.Write(number);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>The PNG chunk checksum (CRC-32) over the type and the data.</summary>
    public static uint Crc(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
