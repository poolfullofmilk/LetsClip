using LetsClip.Data.Helpers;
using LetsClip.Services.Media;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace LetsClip.Desktop.Components.Pages;

public partial class ClipEditor
{
    #region Title
    private void TypeTitle(string text)
    {
        // Shown At Once, The File Takes It When Used
        _titleText = text;
        if (_clip is { } clip)
        {
            _clip = clip with { CustomTitle = clip.CustomTitleFrom(text) };
        }
    }

    private void LeaveTitle()
    {
        // Trimmed Like It Will Be Kept, Nothing Locks Meanwhile
        _titleText = _clip?.CustomTitle ?? "";
    }

    private bool IsRenaming => _clip is { } clip && clip.CustomTitle != _savedTitle;

    private async Task<string?> SettledPathAsync()
    {
        // A Typed Name Reaches The File Before Anything Uses It
        if (IsRenaming && _clip is { } clip && _path is { } path)
        {
            await ReplaceFileAsync(() => ClipLibrary.RenameAsync(path, clip.CustomTitle));

            // The Renamed Clip Shows In Its View Transition, Then Acts
            for (var waited = 0; _swapping && waited < 50; waited++)
            {
                await Task.Delay(100);
            }
        }

        return _swapping ? null : _path;
    }

    private void RenameLeftClip()
    {
        // A Name Typed Here Lands Once The Clip Is Left
        if (IsRenaming && _clip is { } clip && _path is { } path)
        {
            var exports = Controller.Exports;
            _ = Task.Run(() => RenameAfterLeavingAsync(exports, path, clip.CustomTitle));
        }
    }

    private async Task TitleKeyAsync(KeyboardEventArgs key)
    {
        // Enter Or Escape Ends Typing Like Leaving The Field
        if (key.Key is "Enter" or "Escape" && _titleField is { } field)
        {
            await field.BlurAsync();
        }
    }
    #endregion

    #region Cut And Restore
    private Task<bool> CutHereAsync(TrimRange range)
    {
        // An Export That Ended Here Cuts Through The Player
        return _path is { } path ? SaveCutAsync(path, range) : Task.FromResult(false);
    }

    private async Task<bool> SaveCutAsync(string path, TrimRange range)
    {
        // Cut To The Kept Part, The Original Kept If Asked
        if (path != _path)
        {
            return false;
        }

        var keepOriginal = !Controller.Settings.DeleteOriginals;
        _cutting = true;
        StateHasChanged();
        var cut = await ReplaceFileAsync(async () =>
        {
            await ClipLibrary.CutAsync(path, range, keepOriginal, CancellationToken.None);
            return path;
        });
        _cutting = false;
        StateHasChanged();
        if (cut)
        {
            Snackbar.Add(
                keepOriginal
                    ? "Saved the cut, Restore Original brings back the whole clip"
                    : "Saved the cut",
                Severity.Success
            );
        }

        return cut;
    }

    private bool CanSaveCut()
    {
        // Only A Shorter Selection Has Anything To Cut
        return Editable && !Exporting && _range.CutsShort(_duration);
    }

    private async Task RestoreOriginalAsync()
    {
        if (await SettledPathAsync() is not { } path)
        {
            return;
        }

        _restoring = true;
        var restored = await ReplaceFileAsync(async () =>
        {
            await ClipLibrary.RestoreAsync(path);
            return path;
        });
        _restoring = false;
        if (restored)
        {
            Snackbar.Add("Restored the whole clip", Severity.Success);
        }
    }

    private async Task<bool> ReplaceFileAsync(Func<Task<string>> replace)
    {
        // One At A Time, Nothing May Reattach Meanwhile
        if (_swapping)
        {
            return false;
        }

        // Pending Writes Land First, Then The Player Lets Go
        (_swapping, _paused) = (true, true);
        SaveNow();
        await DetachAsync(holdFrame: true);
        var replaced = false;
        try
        {
            var replacedPath = await Task.Run(replace);
            replaced = true;
            if (_path is { } changed)
            {
                Clips.Replaced(changed);
            }

            // A Rename Opens The Clip Under Its New Name
            if (Path.GetFileName(replacedPath) is var name && name != _loadedName)
            {
                if (_path is { } renamed)
                {
                    Controller.Exports.Renamed(renamed, replacedPath);
                }

                _loadedName = name;
                NavigationManager.NavigateTo(Clips.EditorOf(replacedPath), replace: true);
            }
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or TimeoutException
            )
        {
            s_logger.Warn($"Failed To Replace {_loadedName} {exception.GetType().Name}");
            Snackbar.Add("The clip could not be changed, it may be in use", Severity.Warning);
        }

        await LoadAsync(opening: false);
        return replaced;
    }
    #endregion
}
