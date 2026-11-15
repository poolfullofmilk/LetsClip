using LetsClip.Data.Helpers;
using NLog;

namespace LetsClip.Services.Media;

public sealed record ExportOutcome(
    ExportRequest Request,
    string? File,
    string? Problem,
    bool SavedCut
);

public sealed class ExportRunner
{
    public const string CutPhase = "Saving the Cut";

    private const string CutProblem = "Exported, but the clip could not be cut, it may be in use";

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();
    private CancellationTokenSource? _cancel;

    // One At A Time, Exports Pressed Meanwhile Wait In Line
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly List<ExportRequest> _waiting = [];

    // Started Or Ended, Pages Redraw
    public event Action? Changed;

    // Whole Percents, Only Progress Redraws
    public event Action? Progressed;

    public event Action<ExportOutcome>? Finished;

    public ExportRequest? Running { get; private set; }

    public ExportProgress Progress { get; private set; } = new("Starting", 0);

    // The Newest File, Its Clip Shows It Again
    public ExportOutcome? Last { get; private set; }

    // The Open Editor Cuts Its Clip, It Holds The File
    public (string Path, Func<TrimRange, Task<bool>> Cut)? Editor { get; set; }

    public IReadOnlyList<ExportRequest> Waiting => _waiting;

    // Running Or Waiting, The Page And Exit Wait For Both
    public bool IsBusy => Running is not null || _waiting.Count > 0;

    public bool IsExporting(string? path)
    {
        return path is not null && (Running?.SourcePath == path || IsWaiting(path));
    }

    public bool IsWaiting(string? path)
    {
        return _waiting.Any(request => request.SourcePath == path);
    }

    public void Renamed(string from, string to)
    {
        // A Renamed Clip Keeps Showing Its Last Export
        if (Last is { } last && last.Request.SourcePath == from)
        {
            Last = last with { Request = last.Request with { SourcePath = to } };
        }
    }

    public async Task RunAsync(ExportRequest request, bool saveCut, bool keepOriginal)
    {
        // A Clip Already In Line Is Not Added Twice
        if (IsExporting(request.SourcePath))
        {
            return;
        }

        _waiting.Add(request);
        Changed?.Invoke();
        await _turn.WaitAsync();

        // Cancelled While It Waited, The Next One Goes
        if (!_waiting.Remove(request))
        {
            _turn.Release();
            return;
        }

        Running = request;
        Progress = new("Starting", 0);
        _cancel = new CancellationTokenSource();
        Changed?.Invoke();

        // Reports Land Where The Export Started, The UI Thread
        var progress = new Progress<ExportProgress>(report =>
        {
            Progress = report;
            Progressed?.Invoke();
        });
        var outcome = new ExportOutcome(request, null, null, false);
        try
        {
            var token = _cancel.Token;
            var file = await Task.Run(
                () => ClipExporter.ExportAsync(request, progress, token),
                token
            );
            outcome = outcome with { File = file };
            if (saveCut)
            {
                Progress = new(CutPhase, 1);
                Progressed?.Invoke();
                outcome = await CutAsync(outcome, keepOriginal);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled, Nothing Kept, Nothing Wrong
        }
        catch (ExportException exception)
        {
            outcome = outcome with { Problem = exception.Message };
        }
        catch (TimeoutException)
        {
            s_logger.Warn("Failed To Export, It Stopped Answering");
            outcome = outcome with { Problem = "Export stopped answering, nothing was kept" };
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            Running = null;
            _turn.Release();
        }

        if (outcome.File is { } exported)
        {
            // Remembered With Its Clip, Shown Again After A Restart
            Last = outcome;
            ClipLibrary.UpdateMetadata(
                request.SourcePath,
                stored => stored with { Export = Path.GetFileName(exported) }
            );
        }

        Changed?.Invoke();
        Finished?.Invoke(outcome);
    }

    public void Cancel(string? path = null)
    {
        // A Waiting Clip Leaves The Line, No Path Stops All
        if (path is null)
        {
            _waiting.Clear();
        }
        else if (_waiting.RemoveAll(request => request.SourcePath == path) > 0)
        {
            Changed?.Invoke();
            return;
        }
        else if (Running?.SourcePath != path)
        {
            return;
        }

        _cancel?.Cancel();
    }

    private async Task<ExportOutcome> CutAsync(ExportOutcome outcome, bool keepOriginal)
    {
        var request = outcome.Request;

        // The Open Editor Releases The File And Reports Itself
        if (Editor is { } editor && editor.Path == request.SourcePath)
        {
            await editor.Cut(request.Range);
            return outcome;
        }

        try
        {
            await Task.Run(() =>
                ClipLibrary.CutAsync(
                    request.SourcePath,
                    request.Range,
                    keepOriginal,
                    CancellationToken.None
                )
            );
            return outcome with { SavedCut = true };
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or TimeoutException
            )
        {
            s_logger.Warn(
                $"Failed To Cut {Path.GetFileName(request.SourcePath)} {exception.GetType().Name}"
            );
            return outcome with { Problem = CutProblem };
        }
    }
}
