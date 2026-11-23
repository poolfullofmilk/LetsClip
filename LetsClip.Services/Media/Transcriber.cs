using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text.Json;
using LetsClip.Data.Helpers;
using LetsClip.Data.ViewModels;
using LetsClip.Services.Capture;
using NLog;

namespace LetsClip.Services.Media;

public sealed class Transcriber(ClipLibrary library, Func<bool> gameRecords) : IDisposable
{
    // LetsClip's Own Whisper Build And Models, Checked By Hash
    private const string ZipUrl =
        "https://github.com/poolfullofmilk/LetsClip/releases/download/whisper/whisper.zip";
    private const string ZipHash =
        "CEC2B2E651E04250C99C777D510A19924E7B9C3BCE9AE21DF5391FBC69D47755";
    private const string Model = "ggml-small-q5_1.bin";
    private const string Vad = "ggml-silero-v6.2.0.bin";

    // Launch Settles First, Then One Clip At A Time
    private static readonly TimeSpan s_firstWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_busyWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_retryWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_idleWait = TimeSpan.FromMinutes(10);

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private static readonly HttpClient s_client = new() { Timeout = TimeSpan.FromMinutes(30) };

    private static readonly string s_folder = Path.Combine(AppInfo.LocalDirectory, "Whisper");
    private static readonly string s_engine = Path.Combine(s_folder, "whisper");
    private static readonly string s_ready = Path.Combine(s_folder, $"ready-{ZipHash[..12]}");

    // One Recognition At A Time, The Editor's Ask Goes First
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly HashSet<string> _failed = [with(StringComparer.OrdinalIgnoreCase)];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _broken;

    // Each Clip Heard, So Searches And Captions Can Follow
    public event Action<string>? Transcribed;

    // An Old Processor Without AVX2 Runs Nothing Of It
    public static bool CanRun => Avx2.IsSupported;

    public void Start()
    {
        library.Changed += OnLibraryChanged;
        _ = Task.Run(() => RunAsync(_closing.Token));
    }

    private void OnLibraryChanged()
    {
        Interlocked
            .Exchange(
                ref _changed,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            )
            .TrySetResult();
    }

    private async Task RunAsync(CancellationToken token)
    {
        // A Loop That Never Dies, Waiting Out Games And Failures
        await Task.Delay(s_firstWait, token).ConfigureAwait(false);
        var failing = false;
        while (!token.IsCancellationRequested && CanRun && !_broken)
        {
            try
            {
                var changed = Volatile.Read(ref _changed).Task;
                var waiting = (await library.ListAsync(token).ConfigureAwait(false))
                    .Where(clip => clip.Transcript is null && !_failed.Contains(clip.Path))
                    .OrderByDescending(clip => clip.RecordedAt)
                    .ToList();
                if (waiting.Count == 0)
                {
                    await Task.WhenAny(changed, Task.Delay(s_idleWait, token))
                        .ConfigureAwait(false);
                    continue;
                }

                foreach (var clip in waiting)
                {
                    // A Game Recording Waits For Nothing Of This
                    while (gameRecords() && !token.IsCancellationRequested)
                    {
                        await Task.Delay(s_busyWait, token).ConfigureAwait(false);
                    }

                    if (!await EnsureEngineAsync(token).ConfigureAwait(false))
                    {
                        await Task.Delay(s_retryWait, token).ConfigureAwait(false);
                        break;
                    }

                    await TranscribeAsync(clip.Path, gently: true, token).ConfigureAwait(false);
                }

                failing = false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Logged Once Per Failure Streak, Tried Again Later
                if (!failing)
                {
                    s_logger.Warn($"Failed To Transcribe Clips {exception.GetType().Name}");
                }

                failing = true;
                await Task.Delay(s_retryWait, token).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<SpokenWord>?> TranscribeNowAsync(
        string path,
        CancellationToken token
    )
    {
        // The Editor Waits For These, At Full Speed
        if (!CanRun || _broken || !await EnsureEngineAsync(token))
        {
            return null;
        }

        await TranscribeAsync(path, gently: false, token);
        return ClipLibrary.MetadataOf(path)?.Words;
    }

    private async Task TranscribeAsync(string path, bool gently, CancellationToken token)
    {
        await _turn.WaitAsync(token).ConfigureAwait(false);
        var folder = CacheFolders.NewFolder();
        try
        {
            // Heard Already While Waiting Its Turn
            var before = new FileInfo(path);
            if (!before.Exists || ClipLibrary.MetadataOf(path) is not { Words: null })
            {
                return;
            }

            var started = DateTime.UtcNow;
            List<SpokenWord>? words;
            try
            {
                var media = await MediaProbe.ProbeAsync(path, token).ConfigureAwait(false);
                words = media is { HasAudio: true }
                    ? await HearAsync(path, media, folder, gently, token).ConfigureAwait(false)
                    : [];
            }
            catch (Exception exception) when (exception is TimeoutException or Win32Exception)
            {
                // A Clip That Hangs Is Left Until The Next Start
                s_logger.Warn(
                    $"Failed To Transcribe {Path.GetFileName(path)} {exception.GetType().Name}"
                );
                words = null;
            }
            if (words is null)
            {
                _failed.Add(path);
                return;
            }

            // Only The Same File Takes What Was Heard From It
            var after = new FileInfo(path);
            if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
            {
                return;
            }

            ClipLibrary.UpdateMetadata(path, metadata => metadata with { Words = [.. words] });
            s_logger.Info(
                $"Transcribed {Path.GetFileName(path)}, {words.Count} Words In {(DateTime.UtcNow - started).TotalSeconds:0.0} s"
            );
            Transcribed?.Invoke(path);
        }
        finally
        {
            CacheFolders.Delete(folder);
            _turn.Release();
        }
    }

    private async Task<List<SpokenWord>?> HearAsync(
        string path,
        MediaInfo media,
        string folder,
        bool gently,
        CancellationToken token
    )
    {
        // Microphone And Voice Chat, Else The Mix, 16 kHz Mono
        var speech = new[] { AudioTracks.Microphone, AudioTracks.Voice }
            .Select(media.TrackOf)
            .OfType<int>()
            .ToList();
        var graph =
            speech.Count > 1
                ? string.Concat(speech.Select(track => $"[0:a:{track}]"))
                    + $"amix=inputs={speech.Count}:normalize=0,aresample=16000[speech]"
                : $"[0:a:{(speech.Count == 1 ? speech[0] : 0)}]aresample=16000[speech]";
        var wave = Path.Combine(folder, "speech.wav");
        var extracted = await FFmpeg.RunAsync(
            FFmpeg.FFmpegPath,
            [
                "-hide_banner",
                "-nostdin",
                "-loglevel",
                "error",
                "-y",
                "-i",
                path,
                "-filter_complex",
                graph,
                "-map",
                "[speech]",
                "-ac",
                "1",
                "-c:a",
                "pcm_s16le",
                "-f",
                "wav",
                wave,
            ],
            FFmpeg.CopyLimit(new FileInfo(path).Length),
            token,
            gently: gently
        );
        if (!extracted.Succeeded)
        {
            return null;
        }

        // Each Word Its Own Segment, Silence Skipped Before Hearing
        var threads = Math.Max(2, Environment.ProcessorCount / (gently ? 2 : 1));
        var heard = await FFmpeg.RunAsync(
            Path.Combine(s_engine, "whisper-cli.exe"),
            [
                "-m",
                Path.Combine(s_engine, Model),
                "-f",
                wave,
                "-l",
                "auto",
                "-t",
                threads.ToString(CultureInfo.InvariantCulture),
                "-ml",
                "1",
                "-sow",
                "-oj",
                "-of",
                Path.Combine(folder, "speech"),
                "-np",
                "--vad",
                "-vm",
                Path.Combine(s_engine, Vad),
            ],
            TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(media.Duration * 3),
            token,
            gently: gently
        );
        var json = Path.Combine(folder, "speech.json");
        if (!heard.Succeeded || !File.Exists(json))
        {
            // An Illegal Instruction Or Crash Stops Hearing This Run
            if (heard.ExitCode is < 0 or > 255)
            {
                s_logger.Warn($"Stopped Transcribing, The Engine Ended With {heard.ExitCode}");
                _broken = true;
            }

            return null;
        }

        return WordsOf(await File.ReadAllTextAsync(json, token));
    }

    private static List<SpokenWord> WordsOf(string json)
    {
        // Offsets In Milliseconds, Noises And Blanks In Brackets Left Out
        using var document = JsonDocument.Parse(json);
        List<SpokenWord> words = [];
        foreach (var segment in document.RootElement.GetProperty("transcription").EnumerateArray())
        {
            var text = segment.GetProperty("text").GetString()?.Trim() ?? "";
            var offsets = segment.GetProperty("offsets");
            if (
                text.Length == 0
                || text[0] is '[' or '('
                || text.All(character => !char.IsLetterOrDigit(character))
            )
            {
                continue;
            }

            words.Add(
                new SpokenWord(
                    offsets.GetProperty("from").GetDouble() / 1000,
                    offsets.GetProperty("to").GetDouble() / 1000,
                    text
                )
            );
        }

        return words;
    }

    private static async Task<bool> EnsureEngineAsync(CancellationToken token)
    {
        // Fetched Once, Refused Unless Its Hash Matches
        if (File.Exists(s_ready))
        {
            return true;
        }

        var download = Path.Combine(Path.GetTempPath(), $"LetsClip-{Guid.NewGuid():N}.zip");
        var unpacked = Path.Combine(s_folder, $"unpacked-{Guid.NewGuid():N}");
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, ZipUrl))
            {
                request.Headers.UserAgent.ParseAdd(AppInfo.DisplayName);
                using var response = await s_client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(download);
                await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
            }

            await using (var check = File.OpenRead(download))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, token));
                if (hash != ZipHash)
                {
                    s_logger.Warn("Refused The Speech Download, Its Hash Did Not Match");
                    return false;
                }
            }

            ZipFile.ExtractToDirectory(download, unpacked);
            if (Directory.Exists(s_engine))
            {
                Directory.Delete(s_engine, recursive: true);
            }

            Directory.Move(Path.Combine(unpacked, "whisper"), s_engine);
            await File.WriteAllTextAsync(s_ready, ZipHash, token).ConfigureAwait(false);
            s_logger.Info("Downloaded Speech Recognition");
            return true;
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or TaskCanceledException
            )
        {
            // Offline Or Blocked, Tried Again Later
            s_logger.Warn($"Failed To Download Speech Recognition {exception.GetType().Name}");
            return false;
        }
        finally
        {
            TryDelete(download);
            if (Directory.Exists(unpacked))
            {
                try
                {
                    Directory.Delete(unpacked, recursive: true);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    // A Leftover Folder Goes With The Next Download
                }
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A Temporary File Left Behind Harms Nothing
        }
    }

    public void Dispose()
    {
        library.Changed -= OnLibraryChanged;
        _closing.Cancel();
        _closing.Dispose();
        _turn.Dispose();
        GC.SuppressFinalize(this);
    }
}
