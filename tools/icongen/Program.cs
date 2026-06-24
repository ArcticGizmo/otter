using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Builds Otter's multi-size icon.ico from the master src/icon.png. The PNG is the hand-authored
// source of truth (a transparent otter); this tool just packs it into the sizes Windows wants.
//
//   dotnet run -- ..\..\src     # reads icon.png there, writes icon.ico beside it

string dir = args.Length > 0 ? args[0] : ".";
string srcPng = Path.Combine(dir, "icon.png");
if (!File.Exists(srcPng))
{
    Console.Error.WriteLine($"missing master image: {srcPng}");
    return 1;
}

using var master = new Bitmap(srcPng);

static Bitmap Scaled(Bitmap src, int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
    g.SmoothingMode     = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);
    g.DrawImage(src, new Rectangle(0, 0, size, size));
    return bmp;
}

int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
var pngs = new List<byte[]>();
foreach (var s in sizes)
{
    using var b = Scaled(master, s);
    using var ms = new MemoryStream();
    b.Save(ms, ImageFormat.Png);
    pngs.Add(ms.ToArray());
}

using (var fs = new FileStream(Path.Combine(dir, "icon.ico"), FileMode.Create))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0);            // reserved
    w.Write((short)1);            // type = icon
    w.Write((short)sizes.Length); // count

    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        int s = sizes[i];
        w.Write((byte)(s >= 256 ? 0 : s)); // width  (0 = 256)
        w.Write((byte)(s >= 256 ? 0 : s)); // height
        w.Write((byte)0);                  // palette
        w.Write((byte)0);                  // reserved
        w.Write((short)1);                 // planes
        w.Write((short)32);                // bit count
        w.Write(pngs[i].Length);           // bytes in res
        w.Write(offset);                   // offset
        offset += pngs[i].Length;
    }
    foreach (var data in pngs) w.Write(data);
}

Console.WriteLine($"wrote icon.ico ({sizes.Length} sizes) from {srcPng}");
return 0;
