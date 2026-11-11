using LetsClip.Data.ViewModels;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Media;

public partial class SourceChip
{
    [Parameter]
    public required ClipInfo Clip { get; set; }

    // Filled Dark Over A Thumbnail, Plain Beside A Title
    [Parameter]
    public bool OnPicture { get; set; }
}
