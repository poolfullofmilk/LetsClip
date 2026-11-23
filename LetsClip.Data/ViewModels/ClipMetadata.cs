using System.Text.Json.Serialization;
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
    int? CropShape = null,
    double? CropY = null,
    double? CropSize = null,
    CropBox? CropArea = null,
    bool Shrunk = false,
    SpokenWord[]? Words = null
)
{
    // Older Clips Kept A Shape, CropShape 2 Is Square
    [JsonIgnore]
    public CropBox? Box =>
        CropArea?.Clamped()
        ?? (
            Crop is { } x && Width > 0 && Height > 0
                ? CropBox.OfShape(
                    CropShape == 2 ? 1 : 9 / 16.0,
                    x,
                    CropY ?? 0.5,
                    CropSize ?? 1,
                    Width,
                    Height
                )
                : null
        );

    public ClipMetadata WithBox(CropBox? box)
    {
        return this with
        {
            CropArea = box,
            Crop = null,
            CropShape = null,
            CropY = null,
            CropSize = null,
        };
    }

    // Named, Cut, Cropped Or Trimmed, Only Unedited Leaves It Out
    [JsonIgnore]
    public bool IsEdited =>
        Title is not null
        || Cut
        || CropArea is not null
        || Crop is not null
        || (
            TrimStart is { } start
            && TrimEnd is { } end
            && new TrimRange(start, end).CutsShort(Duration)
        );
}
