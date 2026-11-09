using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NLog;

namespace LetsClip.Services.Media;

public sealed record ProcessResult(int ExitCode, string Output, string? LastErrorLine)
{
    public bool Succeeded => ExitCode == 0;

    public string LastError => LastErrorLine ?? $"Exit code {ExitCode}";
}

public static partial class FFmpeg
{
    // Bundled With The App, Never Taken From PATH
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "FFmpeg");
    public static readonly string FFmpegPath = Path.Combine(Folder, "ffmpeg.exe");
    public static readonly string FFprobePath = Path.Combine(Folder, "ffprobe.exe");

    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    // A Killed Process's Pipes Close At Once, A Limit
    private static readonly TimeSpan s_drainLimit = TimeSpan.FromSeconds(2);

    // Children Die With The App, Even After A Crash
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;
    private static readonly nint s_job = CreateKillOnCloseJob();

    // Open Files Survive Temp Cleaners, FFmpeg Sits In %Temp%
    private static List<FileStream> s_heldFiles = [];

    // Windows Throttles A Shown App's Children, Capture Then Drops Frames
    private const int ProcessPowerThrottling = 4;
    private const uint ThrottleExecutionSpeed = 0x1;
    private const uint ThrottleTimerResolution = 0x4;

    public static bool IsAvailable => File.Exists(FFmpegPath) && File.Exists(FFprobePath);

    public static void HoldFiles()
    {
        try
        {
            s_heldFiles =
            [
                .. Directory
                    .EnumerateFiles(Folder)
                    .Select(path => new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read
                    )),
            ];
            s_logger.Debug($"Holding {s_heldFiles.Count} FFmpeg Files Open");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only A Cleaner Could Now Remove Them, Recording Still Runs
            s_logger.Warn($"Failed To Hold FFmpeg Files {exception.GetType().Name}");
        }
    }

    public static Process Start(
        string executable,
        IEnumerable<string> arguments,
        bool redirectInput = false,
        string? workingDirectory = null
    )
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Folder,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var starting = Stopwatch.GetTimestamp();
        var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed To Start {executable}");
        _ = AssignProcessToJobObject(s_job, process.Handle);
        KeepFullSpeed(process.Handle);

        // The Exact Command, And A Start Slower Than 100 ms
        var took = Stopwatch.GetElapsedTime(starting).TotalMilliseconds;
        s_logger.Debug(
            $"Started {Path.GetFileName(executable)} {process.Id}{(took >= 100 ? $" In {took:0} ms" : "")}: {string.Join(' ', startInfo.ArgumentList.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument))}"
        );
        return process;
    }

    private static void KeepFullSpeed(nint process)
    {
        // Control Bits Set With State Clear Opt Out Of EcoQoS
        var state = new PowerThrottlingState
        {
            Version = 1,
            ControlMask = ThrottleExecutionSpeed | ThrottleTimerResolution,
            StateMask = 0,
        };
        _ = SetProcessInformation(
            process,
            ProcessPowerThrottling,
            ref state,
            (uint)Marshal.SizeOf<PowerThrottlingState>()
        );
    }

    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken token,
        Action<string>? errorLine = null,
        string? workingDirectory = null
    )
    {
        using var process = Start(executable, arguments, false, workingDirectory);
        var started = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);

        // Both Pipes Drain Together, Only The Last Error Line Kept
        string? lastError = null;
        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = ReadLinesAsync(
            process.StandardError,
            line =>
            {
                // Progress Pairs Like progress=continue Are Never The Error
                if (!string.IsNullOrWhiteSpace(line) && !IsProgress(line))
                {
                    lastError = line.Trim();
                }

                errorLine?.Invoke(line);
            }
        );
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAny(Task.WhenAll(outputTask, errorTask), Task.Delay(s_drainLimit));
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} Took Too Long");
        }

        var output = await outputTask;
        await errorTask;
        s_logger.Debug(
            $"{Path.GetFileName(executable)} {process.Id} Exited {process.ExitCode} In {started.ElapsedMilliseconds} ms{(process.ExitCode == 0 ? "" : $", {lastError}")}"
        );
        return new ProcessResult(process.ExitCode, output, lastError);
    }

    public static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        // Progress Lines End In A Carriage Return, ReadLine Handles It
        while (await reader.ReadLineAsync() is { } line)
        {
            onLine(line);
        }
    }

    public static async Task DeleteOutputAsync(string path)
    {
        // A Killed Child's File Stays Locked A Moment
        for (var attempt = 1; File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 20)
                {
                    // Its Temporary Name Keeps It Out Of The Library
                    s_logger.Warn(
                        $"Failed To Delete An Unfinished File {exception.GetType().Name}"
                    );
                    return;
                }

                await Task.Delay(100);
            }
        }
    }

    public static void Kill(Process process)
    {
        try
        {
            // FFmpeg Starts No Children, An Exited One Is Left Alone
            process.Kill();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already Disposed, Or Windows Refused While It Was Exiting
        }
    }

    public static string Seconds(double value)
    {
        // Invariant Seconds To The Microsecond
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static bool IsProgress(string line)
    {
        // One Pair With No Spaces, Like out_time_us=1000
        var trimmed = line.Trim();
        return trimmed.Contains('=') && !trimmed.Contains(' ');
    }

    private static nint CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(0, null);
        var limits = new ExtendedLimits { LimitFlags = KillOnJobClose };
        _ = SetInformationJobObject(
            job,
            ExtendedLimitInformation,
            ref limits,
            (uint)Marshal.SizeOf<ExtendedLimits>()
        );
        return job;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct ExtendedLimits
    {
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION On 64 Bit
        [FieldOffset(16)]
        public uint LimitFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(
        nint process,
        int informationClass,
        ref PowerThrottlingState information,
        uint size
    );

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint job,
        int informationClass,
        ref ExtendedLimits information,
        uint length
    );

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);
}
