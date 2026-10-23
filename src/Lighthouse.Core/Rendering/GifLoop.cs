namespace Lighthouse.Core.Rendering;

/// <summary>
/// Makes a multi-frame GIF loop forever at a set frame time. WPF's GIF encoder writes the frames but no loop block
/// and no frame delays, so this rewrites the file: the header and global colours as they were, the NETSCAPE2.0 loop
/// block, then each frame behind a timing block of its own (any timing or loop blocks already there are dropped).
/// </summary>
public static class GifLoop
{
    public static byte[] Make(byte[] gif, int delayCentiseconds)
    {
        if (gif.Length < 13 || gif[0] != 'G' || gif[1] != 'I' || gif[2] != 'F') throw new InvalidDataException("not a GIF");
        int pos = 13;
        if ((gif[10] & 0x80) != 0) pos += ColourTableBytes(gif[10]);
        var output = new List<byte>(gif.Length + 32 * 16);
        output.AddRange(gif[..pos]);
        output[3] = (byte)'8'; // GIF89a: the version with extension blocks
        output[4] = (byte)'9';
        output[5] = (byte)'a';
        output.AddRange([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]); // loop forever
        while (pos < gif.Length)
        {
            int start = pos;
            switch (gif[pos])
            {
                case 0x3B: // trailer
                    output.Add(0x3B);
                    return [.. output];
                case 0x21: // an extension: kept unless it is timing (0xF9) or another loop block
                    byte label = gif[pos + 1];
                    pos = SkipSubBlocks(gif, pos + 2);
                    if (label != 0xF9 && !IsLoopBlock(gif, start)) output.AddRange(gif[start..pos]);
                    break;
                case 0x2C: // a frame: descriptor, local colours, LZW code size, data
                    byte flags = gif[pos + 9];
                    pos += 10;
                    if ((flags & 0x80) != 0) pos += ColourTableBytes(flags);
                    pos = SkipSubBlocks(gif, pos + 1);
                    // Disposal "leave in place", no transparency, this frame's time in 1/100 s.
                    output.AddRange([0x21, 0xF9, 0x04, 0x04, (byte)delayCentiseconds, (byte)(delayCentiseconds >> 8), 0x00, 0x00]);
                    output.AddRange(gif[start..pos]);
                    break;
                default:
                    throw new InvalidDataException($"unexpected GIF block 0x{gif[pos]:X2} at {pos}");
            }
        }
        throw new InvalidDataException("GIF has no trailer");
    }

    private static int ColourTableBytes(byte flags) => 3 << ((flags & 7) + 1);

    private static int SkipSubBlocks(byte[] gif, int pos)
    {
        while (gif[pos] != 0) pos += gif[pos] + 1;
        return pos + 1;
    }

    private static bool IsLoopBlock(byte[] gif, int start) =>
        gif[start + 1] == 0xFF && gif[start + 2] == 11 && gif.AsSpan(start + 3, 8).SequenceEqual("NETSCAPE"u8);
}
