using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Lighthouse.Core.Rendering;

namespace Lighthouse.Tests;

[Collection("GDI handles")] // draws and decodes with GDI+: not alongside the handle-leak count
public class ApngTests
{
    private static byte[] Frame(Color color)
    {
        using var bitmap = new Bitmap(4, 3, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(color);
        bitmap.SetPixel(0, 0, Color.Transparent); // a see-through corner
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [Fact]
    public void Frames_become_one_looping_animation_in_order()
    {
        byte[] apng = Apng.Make([Frame(Color.Red), Frame(Color.Lime), Frame(Color.Blue)], 1, 12);
        var chunks = Apng.Chunks(apng);
        var types = chunks.Select(c => c.Type).ToList();
        Assert.Equal("IHDR", types[0]);
        Assert.True(types.IndexOf("acTL") < types.IndexOf("IDAT"), "the animation is declared before any image data");
        Assert.Equal("IEND", types[^1]);
        Assert.Equal(3, types.Count(t => t == "fcTL"));

        var actl = chunks.Single(c => c.Type == "acTL").Data;
        Assert.Equal((3, 0), (BinaryPrimitives.ReadInt32BigEndian(actl), BinaryPrimitives.ReadInt32BigEndian(actl.AsSpan(4))));
        // Sequence numbers run 0, 1, 2... across frame headers and frame data, as the format requires.
        var sequence = chunks.Where(c => c.Type is "fcTL" or "fdAT").Select(c => BinaryPrimitives.ReadInt32BigEndian(c.Data)).ToList();
        Assert.Equal(Enumerable.Range(0, sequence.Count), sequence);
        var first = chunks.First(c => c.Type == "fcTL").Data;
        Assert.Equal((1, 12), (BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(20)), BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(22))));
    }

    [Fact]
    public void Every_checksum_is_right_and_other_programs_still_read_the_first_frame()
    {
        byte[] apng = Apng.Make([Frame(Color.Red), Frame(Color.Lime)], 1, 12);
        for (int at = 8; at < apng.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(apng.AsSpan(at));
            byte[] type = apng[(at + 4)..(at + 8)], data = apng[(at + 8)..(at + 8 + length)];
            Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(apng.AsSpan(at + 8 + length)), Apng.Crc(type, data));
            at += 12 + length;
        }
        using var image = new Bitmap(new MemoryStream(apng));
        Assert.Equal((4, 3), (image.Width, image.Height));
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(2, 2).ToArgb());
        Assert.Equal(0, image.GetPixel(0, 0).A); // the corner stays see-through
    }

    [Fact]
    public void Frames_of_different_sizes_are_refused()
    {
        using var big = new Bitmap(8, 8);
        using var stream = new MemoryStream();
        big.Save(stream, ImageFormat.Png);
        Assert.Throws<InvalidDataException>(() => Apng.Make([Frame(Color.Red), stream.ToArray()], 1, 12));
        Assert.Throws<InvalidDataException>(() => Apng.Chunks(Encoding.ASCII.GetBytes("GIF89a, not a PNG")));
    }
}
