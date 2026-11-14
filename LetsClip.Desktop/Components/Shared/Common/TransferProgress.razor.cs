using LetsClip.Data.Helpers;
using Microsoft.AspNetCore.Components;

namespace LetsClip.Desktop.Components.Shared.Common;

public partial class TransferProgress
{
    // Shown Once Every Byte Is Through
    [Parameter]
    public string? DoneText { get; set; }

    // Bytes So Far, Zero Total While No Length Is Known
    private long _done;
    private long _total;
    private int _percent;

    // Nothing Reads Until The First Bytes Move
    private string Text =>
        _done == 0 ? ""
        : _total > 0 && _done >= _total && DoneText is { } done ? done
        : _total > 0 ? $"{Formatter.FormatSize(_done)} of {Formatter.FormatSize(_total)}"
        : Formatter.FormatSize(_done);

    public void Report((long Done, long Total) progress)
    {
        // Redraws Once Per Percent, Not Per Chunk
        var (done, total) = progress;
        var percent = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
        var moved = total != _total || (total > 0 ? percent != _percent : done - _done >= 1048576);
        if (!moved)
        {
            return;
        }

        (_done, _total, _percent) = (done, total, percent);
        _ = InvokeAsync(StateHasChanged);
    }
}
