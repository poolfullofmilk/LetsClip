using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using LetsClip.Data.ViewModels;
using Drawing = System.Drawing;

namespace LetsClip.Desktop.Interop;

public static class CaptionPainter
{
    // Windows' Own Heaviest Face, Clear At Any Size
    private const string Face = "Segoe UI Black";

    public static CaptionTrack Paint(
        IReadOnlyList<CaptionFrame> frames,
        double length,
        int width,
        int height,
        string folder
    )
    {
        // A Low Band, One Picture Per Change, Blank Between
        var size = (float)(Math.Min(width, height) * 0.075);
        var band = (int)Math.Ceiling(size * 1.9 / 2) * 2;
        var centre = height * (height > width ? 0.62 : 0.76);
        var top = Math.Clamp((int)Math.Round(centre - (band / 2.0)), 0, Math.Max(0, height - band));
        using var family = new Drawing.FontFamily(Face);
        var list = new StringBuilder("ffconcat version 1.0\n");
        using (var empty = new Drawing.Bitmap(width, band, PixelFormat.Format32bppArgb))
        {
            empty.Save(Path.Combine(folder, "blank.png"), ImageFormat.Png);
        }

        var time = 0.0;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame.Start - time > 0.001)
            {
                Add(list, "blank.png", frame.Start - time);
            }

            var name = string.Create(
                CultureInfo.InvariantCulture,
                $"caption-{index + 1:000000}.png"
            );
            PaintCard(frame, width, band, size, family, Path.Combine(folder, name));
            Add(list, name, frame.End - frame.Start);
            time = frame.End;
        }

        Add(list, "blank.png", Math.Max(0.04, length - time));

        // The Last Picture Again, So Its Duration Holds
        list.Append("file 'blank.png'\n");
        var listPath = Path.Combine(folder, "captions.txt");
        File.WriteAllText(listPath, list.ToString());
        return new CaptionTrack(listPath, top);
    }

    private static void Add(StringBuilder list, string name, double seconds)
    {
        list.Append(CultureInfo.InvariantCulture, $"file '{name}'\nduration {seconds:0.000}\n");
    }

    private static void PaintCard(
        CaptionFrame frame,
        int width,
        int band,
        float size,
        Drawing.FontFamily family,
        string path
    )
    {
        // Laid Out Whole So Nothing Moves, Spoken Words Drawn
        using var bitmap = new Drawing.Bitmap(width, band, PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var format = Drawing.StringFormat.GenericTypographic;
        var fitted = size;
        float[] widths;
        float space;
        while (true)
        {
            using var font = new Drawing.Font(
                family,
                fitted,
                Drawing.FontStyle.Regular,
                Drawing.GraphicsUnit.Pixel
            );
            widths =
            [
                .. frame.Words.Select(word =>
                    graphics.MeasureString(word, font, Drawing.PointF.Empty, format).Width
                ),
            ];
            space = fitted * 0.3f;
            var whole = widths.Sum() + (space * (widths.Length - 1));
            if (whole <= width * 0.9f || fitted < size / 2)
            {
                break;
            }

            fitted *= 0.9f;
        }

        var lineHeight =
            fitted
            * family.GetLineSpacing(Drawing.FontStyle.Regular)
            / family.GetEmHeight(Drawing.FontStyle.Regular);
        var y = (band - lineHeight) / 2;
        var x = (width - (widths.Sum() + (space * (widths.Length - 1)))) / 2;
        using var text = new GraphicsPath();
        using var shadow = new GraphicsPath();
        for (var index = 0; index < frame.Spoken; index++)
        {
            text.AddString(
                frame.Words[index],
                family,
                (int)Drawing.FontStyle.Regular,
                fitted,
                new Drawing.PointF(x, y),
                format
            );
            shadow.AddString(
                frame.Words[index],
                family,
                (int)Drawing.FontStyle.Regular,
                fitted,
                new Drawing.PointF(x, y + (fitted * 0.06f)),
                format
            );
            x += widths[index] + space;
        }

        // A Soft Drop, A Dark Edge, Then White
        using (
            var drop = new Drawing.Pen(Drawing.Color.FromArgb(90, 0, 0, 0), fitted * 0.3f)
            {
                LineJoin = LineJoin.Round,
            }
        )
        {
            graphics.DrawPath(drop, shadow);
        }

        using (
            var edge = new Drawing.Pen(Drawing.Color.FromArgb(235, 0, 0, 0), fitted * 0.16f)
            {
                LineJoin = LineJoin.Round,
            }
        )
        {
            graphics.DrawPath(edge, text);
        }

        graphics.FillPath(Drawing.Brushes.White, text);
        bitmap.Save(path, ImageFormat.Png);
    }
}
