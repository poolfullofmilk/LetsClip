using System.Diagnostics;

namespace LetsClip.Data.ViewModels;

public sealed record CropBox(double Left, double Top, double Width, double Height)
{
    // A Tenth Of Each Side, The Grips Still Apart
    public const double SmallestSide = 0.1;

    public static readonly CropBox Whole = new(0, 0, 1, 1);

    static CropBox()
    {
        Debug.Assert(SelfTestPasses());
    }

    public static CropBox OfShape(
        double aspect,
        double x,
        double y,
        double size,
        int width,
        int height
    )
    {
        // Older Clips Kept A Vertical Or Square Part, Placed
        var fullHeight = aspect <= width / (double)height;
        var cropWidth = (fullHeight ? height * aspect : width) * size;
        var cropHeight = (fullHeight ? height : width / aspect) * size;
        return new CropBox(
            (width - cropWidth) * x / width,
            (height - cropHeight) * y / height,
            cropWidth / width,
            cropHeight / height
        ).Clamped();
    }

    public CropBox Clamped()
    {
        // Inside The Frame, Never Smaller Than The Grips Allow
        var width = Math.Clamp(Width, SmallestSide, 1);
        var height = Math.Clamp(Height, SmallestSide, 1);
        return new(Math.Clamp(Left, 0, 1 - width), Math.Clamp(Top, 0, 1 - height), width, height);
    }

    public bool Changes(int width, int height)
    {
        // A Box Over Every Pixel Keeps The Whole Frame
        var (cropWidth, cropHeight, _, _) = PixelsOf(width, height);
        return cropWidth < EvenDown(width) || cropHeight < EvenDown(height);
    }

    public (int Width, int Height, int Left, int Top) PixelsOf(int width, int height)
    {
        // Even Sizes Moving Never Changes, Placed From The Far Edge
        var cropWidth = Math.Min(EvenDown(width * Width), EvenDown(width));
        var cropHeight = Math.Min(EvenDown(height * Height), EvenDown(height));
        var right = Math.Clamp(EvenDown(width * (Left + Width)), cropWidth, EvenDown(width));
        var bottom = Math.Clamp(EvenDown(height * (Top + Height)), cropHeight, EvenDown(height));
        return (cropWidth, cropHeight, right - cropWidth, bottom - cropHeight);
    }

    private static int EvenDown(double value, int smallest = 2)
    {
        return Math.Max(smallest, (int)Math.Round(value, 6) / 2 * 2);
    }

    private static bool SelfTestPasses()
    {
        return Whole.PixelsOf(1920, 1080) == (1920, 1080, 0, 0)
            && !Whole.Changes(1920, 1080)
            && !Whole.Changes(1365, 767)
            && new CropBox(0.25, 0.5, 0.5, 0.5).PixelsOf(1920, 1080) == (960, 540, 480, 540)
            && new CropBox(0.25, 0, 0.5, 1).Changes(1920, 1080)
            && new CropBox(0.9, 0.9, 0.5, 0.01).Clamped() == new CropBox(0.5, 0.9, 0.5, 0.1)
            && OfShape(9 / 16.0, 0.5, 0.5, 1, 1920, 1080).PixelsOf(1920, 1080)
                == (606, 1080, 656, 0)
            && OfShape(1, 0.5, 1, 1, 1080, 1920).PixelsOf(1080, 1920) == (1080, 1080, 0, 840)
            && OfShape(9 / 16.0, 1, 1, 0.5, 1920, 1080).PixelsOf(1920, 1080)
                == (302, 540, 1618, 540)
            && new CropBox(0.3, 0.2, 0.5, 0.5).PixelsOf(1920, 1080).Width
                == new CropBox(0.31, 0.21, 0.5, 0.5).PixelsOf(1920, 1080).Width;
    }
}
