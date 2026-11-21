using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;

namespace LetsClip.Services.Media;

public sealed partial class ClipLibrary
{
    // Windows Writes Version 2: Size, Deletion Time, Length, Path
    private const long InfoVersion = 2;
    private const int InfoTimeOffset = 16;
    private const int InfoLengthOffset = 24;
    private const int InfoPathOffset = 28;

    // Shell Events That Keep Explorer's Recycle Bin Current
    private const int ShellItemDeleted = 0x4;
    private const uint ShellPathFlag = 0x5;

    // Details And Whole Clips Leave With Their Clip Within This
    private static readonly TimeSpan s_companionGap = TimeSpan.FromMinutes(1);

    static ClipLibrary()
    {
        Debug.Assert(RecycleBinSelfTestPasses());
    }

    public static bool RecycleClip(string path)
    {
        // Its Whole Clip And Details Follow, A Restore Returns All
        var original = OwnedOriginalOf(path);
        var details = MetadataPathOf(path);
        if (!Recycle(path))
        {
            return false;
        }

        if (original is not null)
        {
            _ = Recycle(original);
        }

        if (File.Exists(details))
        {
            _ = Recycle(details);
        }

        return true;
    }

    public static IReadOnlyList<DeletedClip> DeletedFrom(string folder)
    {
        // Clips Deleted From This Folder, And Its Cuts' Whole Clips
        var recycled = RecycledFrom(folder);
        var originals = Path.Combine(folder, OriginalsFolder);
        var clips = new List<DeletedClip>();
        foreach (var (info, original, deletedAt) in recycled)
        {
            var parent = Path.GetDirectoryName(original);
            if (!original.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (FilePaths.AreSame(parent, folder))
            {
                var details = Nearest(recycled, MetadataPathOf(original), deletedAt) is { } kept
                    ? ReadJson(RecycledPathOf(kept.Info))
                    : null;
                clips.Add(
                    new DeletedClip(
                        info,
                        original,
                        deletedAt,
                        TitleOf(original, details),
                        RecordedAtOf(original, details, RecycledPathOf(info)),
                        IsWholeClip: false
                    )
                );
            }
            else if (
                FilePaths.AreSame(parent, originals)
                && Path.Combine(folder, Path.GetFileName(original)) is var clip
                && File.Exists(clip)
            )
            {
                // A Cut's Whole Clip, Read As The Clip Still Here
                var metadata = MetadataOf(clip);
                clips.Add(
                    new DeletedClip(
                        info,
                        original,
                        deletedAt,
                        TitleOf(clip, metadata),
                        RecordedAtOf(clip, metadata, clip),
                        IsWholeClip: true
                    )
                );
            }
        }

        return [.. clips.OrderByDescending(clip => clip.DeletedAt)];
    }

    public static string Restore(DeletedClip clip)
    {
        // Back Where It Was, A Taken Name Gets A Number
        var target = Unrecycle(clip.InfoPath, clip.OriginalPath);
        if (!clip.IsWholeClip)
        {
            // Its Details And Whole Clip Return With It
            var recycled = RecycledFrom(Path.GetDirectoryName(clip.OriginalPath)!);
            if (Nearest(recycled, MetadataPathOf(clip.OriginalPath), clip.DeletedAt) is { } details)
            {
                Unrecycle(details.Info, MetadataPathOf(target));
            }

            if (
                Nearest(recycled, OriginalPathOf(clip.OriginalPath), clip.DeletedAt) is { } original
            )
            {
                Unrecycle(original.Info, OriginalPathOf(target));
            }
        }

        // Explorer's Bin And Its Icon Show The Change
        _ = SHUpdateRecycleBinIcon();
        s_logger.Info($"Restored {Path.GetFileName(target)} From The Recycle Bin");
        return target;
    }

    private static string Unrecycle(string info, string wanted)
    {
        // Hidden Folders Come Back Hidden, Details Replace A Stale Copy
        var folder = Directory.CreateDirectory(Path.GetDirectoryName(wanted)!);
        if (folder.Name is OriginalsFolder or DetailsFolder)
        {
            folder.Attributes |= FileAttributes.Hidden;
        }

        var details = folder.Name == DetailsFolder;
        var target =
            details || !File.Exists(wanted)
                ? wanted
                : ClipSaver.UniquePath(
                    folder.FullName,
                    Path.GetFileNameWithoutExtension(wanted),
                    Path.GetExtension(wanted)
                );
        var recycled = RecycledPathOf(info);
        File.Move(recycled, target, overwrite: details);
        TryDelete(info);
        SHChangeNotify(ShellItemDeleted, ShellPathFlag, recycled, 0);
        return target;
    }

    private static List<(string Info, string Original, DateTimeOffset DeletedAt)> RecycledFrom(
        string folder
    )
    {
        // The Person's Own Bin On The Folder's Drive
        var found = new List<(string Info, string Original, DateTimeOffset DeletedAt)>();
        DirectoryInfo bin;
        try
        {
            var user = WindowsIdentity.GetCurrent().User?.Value;
            if (user is null || Path.GetPathRoot(Path.GetFullPath(folder)) is not { } root)
            {
                return found;
            }

            bin = new DirectoryInfo(Path.Combine(root, "$Recycle.Bin", user));
            if (!bin.Exists)
            {
                return found;
            }

            foreach (var info in bin.EnumerateFiles("$I*"))
            {
                if (
                    ReadInfo(info.FullName) is { } file
                    && FilePaths.IsInside(file.Original, folder)
                    && File.Exists(RecycledPathOf(info.FullName))
                )
                {
                    found.Add((info.FullName, file.Original, file.DeletedAt));
                }
            }
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or SecurityException
                        or ArgumentException
            )
        {
            // An Unreadable Bin Lists What It Read So Far
            s_logger.Warn($"Failed To Read The Recycle Bin {exception.GetType().Name}");
        }

        return found;
    }

    private static (string Original, DateTimeOffset DeletedAt)? ReadInfo(string info)
    {
        try
        {
            return InfoOf(File.ReadAllBytes(info));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // One Unreadable Record Leaves Out One File
            return null;
        }
    }

    private static (string Original, DateTimeOffset DeletedAt)? InfoOf(byte[] bytes)
    {
        // A Recycled File's Record, Anything Else Reads As None
        if (bytes.Length <= InfoPathOffset || BitConverter.ToInt64(bytes, 0) != InfoVersion)
        {
            return null;
        }

        var deleted = BitConverter.ToInt64(bytes, InfoTimeOffset);
        var characters = Math.Clamp(
            BitConverter.ToInt32(bytes, InfoLengthOffset),
            0,
            (bytes.Length - InfoPathOffset) / 2
        );
        var original = Encoding
            .Unicode.GetString(bytes, InfoPathOffset, characters * 2)
            .TrimEnd('\0');
        return original.Length == 0 || deleted <= 0 || deleted > DateTime.MaxValue.ToFileTimeUtc()
            ? null
            : (original, DateTimeOffset.FromFileTime(deleted));
    }

    private static string RecycledPathOf(string info)
    {
        // $IABC123.mp4 Holds The Record, $RABC123.mp4 The File
        return Path.Combine(Path.GetDirectoryName(info)!, "$R" + Path.GetFileName(info)[2..]);
    }

    private static (string Info, string Original, DateTimeOffset DeletedAt)? Nearest(
        List<(string Info, string Original, DateTimeOffset DeletedAt)> recycled,
        string original,
        DateTimeOffset deletedAt
    )
    {
        // The Companion That Left Closest To The Clip
        return recycled
            .Where(file =>
                FilePaths.AreSame(file.Original, original)
                && (file.DeletedAt - deletedAt).Duration() <= s_companionGap
            )
            .OrderBy(file => (file.DeletedAt - deletedAt).Duration())
            .Cast<(string, string, DateTimeOffset)?>()
            .FirstOrDefault();
    }

    private static string TitleOf(string clip, ClipMetadata? metadata)
    {
        // Its Given Name, Else Its Source, Else Its File
        var name = Path.GetFileNameWithoutExtension(clip);
        var (game, monitor) = ClipNames.SourceFrom(name);
        return metadata?.Title
            ?? (
                ClipNames.DateOf(name) is null
                    ? name
                    : ClipNames.SourceOf(metadata?.Game ?? game, metadata?.Monitor ?? monitor)
            );
    }

    private static DateTimeOffset RecordedAtOf(string clip, ClipMetadata? metadata, string file)
    {
        // Its Details, Else Its Saved Name, Else Its Write Time
        return metadata?.RecordedAt
            ?? ClipNames.DateOf(Path.GetFileNameWithoutExtension(clip))
            ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(int eventId, uint flags, string item, nint other);

    [LibraryImport("shell32.dll")]
    private static partial int SHUpdateRecycleBinIcon();

    private static bool RecycleBinSelfTestPasses()
    {
        // A Record Windows Wrote, Then Records To Refuse
        const string Original =
            @"C:\Users\Someone\Videos\LetsClip\Overwatch 2026-11-21 10-15-00.mp4";
        var deleted = new DateTimeOffset(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);
        var path = Encoding.Unicode.GetBytes(Original + "\0");
        byte[] record =
        [
            .. BitConverter.GetBytes(InfoVersion),
            .. BitConverter.GetBytes(1_000_000L),
            .. BitConverter.GetBytes(deleted.ToFileTime()),
            .. BitConverter.GetBytes(Original.Length + 1),
            .. path,
        ];
        byte[] older = [.. BitConverter.GetBytes(1L), .. record[8..]];
        return InfoOf(record) is { } read
            && read.Original == Original
            && read.DeletedAt == deleted
            && InfoOf(older) is null
            && InfoOf(record[..InfoPathOffset]) is null
            && InfoOf([.. record[..InfoLengthOffset], .. BitConverter.GetBytes(9999), .. path])
                is { Original: Original }
            && RecycledPathOf(@"C:\$Recycle.Bin\S-1\$IABC123.mp4")
                == @"C:\$Recycle.Bin\S-1\$RABC123.mp4"
            && TitleOf(Original, null) == "Overwatch"
            && TitleOf(@"C:\Clips\Ceiling Shot.mp4", null) == "Ceiling Shot"
            && TitleOf(@"C:\Clips\Monitor 2 2026-11-21 10-15-00 (2).mp4", null) == "Monitor 2";
    }
}
