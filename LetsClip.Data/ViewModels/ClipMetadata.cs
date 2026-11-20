using System.Text.Json.Serialization;
using LetsClip.Data.Enums;
using LetsClip.Data.Helpers;

namespace LetsClip.Data.ViewModels;

public sealed record ClipMetadata(
    DateTimeOffset RecordedAt,
    double Duration,
    double? TrimStart,
    double? TrimEnd,
    long FileSize,
    DateTime FileWriteTimeUtc,
    int? FrameRate = null,
    string? Title = null,
    string? Game = null,
    int Width = 0,
    int Height = 0,
    double? CutStart = null,
    double? CutEnd = null,
    bool ForeignOriginal = false,
    int? Monitor = null,
    bool Cut = false,
    string? Export = null,
    double? Crop = null,
    CropShape? CropShape = null,
    double? CropY = null,
    double? CropSize = null
)
{
    // Older Clips Kept Only Where A Full Height Vertical Sat
    [JsonIgnore]
    public CropBox? Box =>
        Crop is { } x
            ? new CropBox(CropShape ?? Enums.CropShape.Vertical, x, CropY ?? 0.5, CropSize ?? 1)
            : null;

    public ClipMetadata WithBox(CropBox? box)
    {
        return this with
        {
            Crop = box?.X,
            CropY = box?.Y,
            CropSize = box?.Size,
            CropShape = box?.Shape,
        };
    }

    // Named, Cut Or Trimmed Short, Only Unedited Leaves It Out
    public bool IsEdited =>
        Title is not null
        || Cut
        || (
            TrimStart is { } start
            && TrimEnd is { } end
            && new TrimRange(start, end).CutsShort(Duration)
        );
}
