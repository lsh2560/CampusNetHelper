using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconMaker
{
    private static bool IsBlue(Color c)
    {
        return c.B > 95 && c.B > c.R + 25 && c.B > c.G - 18;
    }

    private static bool IsOuterWhite(Color c)
    {
        int brightest = Math.Max(c.R, Math.Max(c.G, c.B));
        int darkest = Math.Min(c.R, Math.Min(c.G, c.B));
        return darkest > 218 && brightest - darkest < 25;
    }

    private static Rectangle FindCrop(Bitmap source)
    {
        int left = source.Width, top = source.Height, right = 0, bottom = 0;
        for (int y = 0; y < source.Height; y += 2)
            for (int x = 0; x < source.Width; x += 2)
                if (IsBlue(source.GetPixel(x, y)))
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        int side = Math.Max(right - left, bottom - top) + 54;
        side = Math.Min(side, Math.Min(source.Width, source.Height));
        int centerX = (left + right) / 2;
        int centerY = (top + bottom) / 2;
        int x0 = Math.Max(0, Math.Min(source.Width - side, centerX - side / 2));
        int y0 = Math.Max(0, Math.Min(source.Height - side, centerY - side / 2));
        return new Rectangle(x0, y0, side, side);
    }

    private static void RemoveOuterBackground(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        bool[] seen = new bool[w * h];
        var pending = new Queue<int>();
        for (int x = 0; x < w; x++) { pending.Enqueue(x); pending.Enqueue((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { pending.Enqueue(y * w); pending.Enqueue(y * w + w - 1); }
        while (pending.Count > 0)
        {
            int index = pending.Dequeue();
            if (seen[index]) continue;
            seen[index] = true;
            int x = index % w, y = index / w;
            Color c = bitmap.GetPixel(x, y);
            if (!IsOuterWhite(c)) continue;
            bitmap.SetPixel(x, y, Color.Transparent);
            if (x > 0) pending.Enqueue(index - 1);
            if (x < w - 1) pending.Enqueue(index + 1);
            if (y > 0) pending.Enqueue(index - w);
            if (y < h - 1) pending.Enqueue(index + w);
        }
    }

    private static Bitmap Resize(Bitmap source, int size)
    {
        var target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(target))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }
        return target;
    }

    private static void Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Usage: IconMaker source.png output.ico preview.png");
        using (var source = new Bitmap(args[0]))
        using (var master = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
        {
            Rectangle crop = FindCrop(source);
            using (Graphics g = Graphics.FromImage(master))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(source, new Rectangle(0, 0, 256, 256), crop, GraphicsUnit.Pixel);
            }
            RemoveOuterBackground(master);
            master.Save(args[2], ImageFormat.Png);

            int[] sizes = { 256, 128, 64, 48, 32, 16 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
            {
                using (Bitmap bitmap = Resize(master, size))
                using (var buffer = new MemoryStream())
                {
                    bitmap.Save(buffer, ImageFormat.Png);
                    images.Add(buffer.ToArray());
                }
            }

            using (var stream = File.Create(args[1]))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)images[i].Length);
                    writer.Write((uint)offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) writer.Write(image);
            }
        }
    }
}
