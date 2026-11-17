using System.Text.Json.Serialization;
using LetsClip.Data.Enums;

namespace LetsClip.Data.ViewModels;

public sealed record GameCapture(
    string Key,
    int? FrameRate = null,
    int? Resolution = null,
    CaptureQuality? Quality = null
)
{
    [JsonIgnore]
    public bool IsDefault => FrameRate is null && Resolution is null && Quality is null;
}
