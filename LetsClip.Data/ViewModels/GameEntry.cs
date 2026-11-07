namespace LetsClip.Data.ViewModels;

public sealed record GameEntry(
    string Key,
    string Name,
    string? Path,
    bool IsRemoved,
    bool IsInstalledGame
);
