using System.Diagnostics;
using LetsClip.Data.Enums;

namespace LetsClip.Data.ViewModels;

public sealed record CropBox(CropShape Shape, double X, double Y, double Size)
{
    // Half The Largest Box, Its Corner Grips Still Apart
    public const double SmallestSize = 0.5;

    static CropBox()
    {
        Debug.Assert(SelfTestPasses());
    }

    // Width Over Height Of The Part Kept
    public double Aspect => Shape == CropShape.Square ? 1 : 9 / 16.0;

    public static CropBox Centred(CropShape shape)
    {
        return new(shape, 0.5, 0.5, 1);
    }

    public static bool Changes(CropShape shape, int width, int height)
    {
        // A Frame Already That Shape Or Narrower Keeps Everything
        return shape switch
        {
            CropShape.Vertical => width * 16 > height * 9,
            CropShape.Square => width != height,
            _ => false,
        };
    }

    public (int Width, int Height, int Left, int Top) PixelsOf(int width, int height)
    {
        // The Largest Part Of This Shape, Scaled, Placed, All Even
        var fullHeight = Aspect <= width / (double)height;
        var cropWidth = EvenDown(fullHeight ? height * Aspect * Size : width * Size);
        var cropHeight = EvenDown(fullHeight ? height * Size : width / Aspect * Size);
        return (
            cropWidth,
            cropHeight,
            EvenDown((width - cropWidth) * X, 0),
            EvenDown((height - cropHeight) * Y, 0)
        );
    }

    private static int EvenDown(double value, int smallest = 2)
    {
        return Math.Max(smallest, (int)Math.Round(value, 6) / 2 * 2);
    }

    private static bool SelfTestPasses()
    {
        return Centred(CropShape.Vertical).PixelsOf(1920, 1080) == (606, 1080, 656, 0)
            && new CropBox(CropShape.Square, 0, 0, 1).PixelsOf(1920, 1080) == (1080, 1080, 0, 0)
            && new CropBox(CropShape.Square, 0.5, 1, 1).PixelsOf(1080, 1920) == (1080, 1080, 0, 840)
            && new CropBox(CropShape.Vertical, 1, 1, 0.5).PixelsOf(1920, 1080)
                == (302, 540, 1618, 540)
            && Changes(CropShape.Vertical, 1920, 1080)
            && !Changes(CropShape.Vertical, 1080, 1920)
            && !Changes(CropShape.Square, 1080, 1080)
            && Changes(CropShape.Square, 1080, 1920)
            && !Changes(CropShape.Off, 1920, 1080);
    }
}
