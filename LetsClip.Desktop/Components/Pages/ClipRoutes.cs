namespace LetsClip.Desktop.Components.Pages;

public static class ClipRoutes
{
    public const string List = "clips";

    public static string EditorOf(string path)
    {
        return $"{List}/{Uri.EscapeDataString(Path.GetFileName(path))}";
    }
}
