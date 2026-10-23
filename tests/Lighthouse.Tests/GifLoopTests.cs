using System.Drawing;
using System.Drawing.Imaging;
using Lighthouse.Core.Rendering;

namespace Lighthouse.Tests;

[Collection("GDI handles")] // decodes with GDI+: not alongside the handle-leak count
public class GifLoopTests
{
    // A 1x1 frame (black and white colours) as a GIF writes it: descriptor, LZW code size, one data block.
    private static readonly byte[] Frame = [0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0x02, 0x02, 0x44, 0x01, 0x00];
    private static readonly byte[] OldTiming = [0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00];

    private static byte[] TwoFrames() =>
        [.. "GIF87a"u8, 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, .. OldTiming, .. Frame, .. Frame, 0x3B];

    private static int Count(byte[] bytes, byte[] pattern) =>
        Enumerable.Range(0, bytes.Length - pattern.Length + 1).Count(i => bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern));

    [Fact]
    public void Every_frame_gets_one_timing_block_and_the_file_loops()
    {
        byte[] gif = GifLoop.Make(TwoFrames(), 8);
        Assert.Equal("GIF89a"u8.ToArray(), gif[..6]);
        Assert.Equal(1, Count(gif, [.. "NETSCAPE2.0"u8]));
        Assert.Equal(2, Count(gif, [0x21, 0xF9, 0x04, 0x04, 8, 0, 0, 0]));
        Assert.Equal(0, Count(gif, OldTiming)); // the old one is replaced, not doubled
        Assert.Equal(0x3B, gif[^1]);
    }

    [Fact]
    public void Windows_reads_it_as_a_looping_animation()
    {
        using var image = Image.FromStream(new MemoryStream(GifLoop.Make(TwoFrames(), 8)));
        Assert.Equal(2, image.GetFrameCount(FrameDimension.Time));
        Assert.Equal(0, BitConverter.ToInt16(image.GetPropertyItem(0x5101)!.Value!)); // loop count 0: forever
        Assert.Equal(8, BitConverter.ToInt32(image.GetPropertyItem(0x5100)!.Value!, 0)); // first frame's delay
    }

    [Fact]
    public void Rejects_what_is_not_a_gif() =>
        Assert.Throws<InvalidDataException>(() => GifLoop.Make("PNG not a gif at all"u8.ToArray(), 8));
}
