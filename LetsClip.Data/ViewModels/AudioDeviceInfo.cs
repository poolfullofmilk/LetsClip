namespace LetsClip.Data.ViewModels;

public sealed record AudioDeviceInfo(string Id, string Name, string? Adapter, bool IsDefault);
