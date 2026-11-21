namespace LetsClip.Data.ViewModels;

public sealed record DeletedClip(
    string InfoPath,
    string OriginalPath,
    DateTimeOffset DeletedAt,
    string Title,
    DateTimeOffset RecordedAt,
    bool IsWholeClip
);
