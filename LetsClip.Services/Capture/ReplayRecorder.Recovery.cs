using LetsClip.Services.Media;

namespace LetsClip.Services.Capture;

public sealed partial class ReplayRecorder
{
    private static bool ConvertsOnGpu(CaptureOptions options, int width, int height)
    {
        // A Window Arrives Sized, A Monitor Only At Full Size
        var monitor = options.Monitor;
        return options.GameWindow != 0
            || (monitor.Rotation == 0 && width == monitor.Width && height == monitor.Height);
    }

    private async Task UpgradeAsync(CaptureSession current)
    {
        // A Fallback Keeps Trying Better Encoders, Out Of Sight
        var better = VideoEncoders
            .CaptureCandidates(
                ConvertsOnGpu(current.Options, current.Size.Width, current.Size.Height)
            )
            .TakeWhile(encoder => encoder != current.Encoder)
            .ToList();
        for (var attempt = 0; better.Count > 0; attempt++)
        {
            var wait = s_upgradeWaits[Math.Min(attempt, s_upgradeWaits.Length - 1)];

            // The Wait Ends With The Session, No Timer Left Behind
            using (var waited = new CancellationTokenSource())
            {
                await Task.WhenAny(Task.Delay(wait, waited.Token), current.Ended);
                waited.Cancel();
            }

            if (current.Stopping || current.HasEnded)
            {
                return;
            }

            foreach (var encoder in better)
            {
                if (await TryMoveToAsync(current, encoder))
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> TryMoveToAsync(CaptureSession current, VideoEncoder encoder)
    {
        // Runs Beside The Fallback For A Replay, Never Mid Recording
        var options = current.Options;
        if (
            RecordingSince is not null
            || !await VideoEncoders.WorksAsync(
                encoder,
                current.Size.Width,
                current.Size.Height,
                options.FrameRate,
                CancellationToken.None
            )
        )
        {
            return false;
        }

        return await TryStandbyAsync(
            current,
            encoder,
            async standby =>
            {
                // A Whole Replay Beside It, The Change Costs No History
                using var waited = new CancellationTokenSource();
                await Task.WhenAny(
                    Task.Delay(
                        TimeSpan.FromSeconds(options.Seconds + CaptureSession.SegmentSeconds),
                        waited.Token
                    ),
                    standby.Ended,
                    current.Ended
                );
                waited.Cancel();
                return !standby.HasEnded;
            },
            isUpgrade: true,
            $"Stayed On {current.Encoder.Name},"
        );
    }

    private async Task<bool> TryStandbyAsync(
        CaptureSession current,
        VideoEncoder encoder,
        Func<CaptureSession, Task<bool>> readyAsync,
        bool isUpgrade,
        string failure
    )
    {
        // Launched Beside The Running Session, Swapped In Only Once Ready
        var standby = new CaptureSession(
            current.Options,
            encoder,
            current.Size.Width,
            current.Size.Height
        );
        try
        {
            await standby.LaunchAsync();
            if (await readyAsync(standby) && await SwapAsync(current, standby, isUpgrade))
            {
                return true;
            }
        }
        catch (Exception exception) when (IsStartFailure(exception))
        {
            s_logger.Info($"{failure} {encoder.Name}: {exception.Message}");
        }

        standby.Shutdown();
        return false;
    }

    private async Task<bool> SwapAsync(
        CaptureSession current,
        CaptureSession standby,
        bool isUpgrade = false
    )
    {
        // Whole Replay Ready, So The Change Costs No History
        await _transition.WaitAsync();
        try
        {
            await WaitForSavesAsync(TimeSpan.FromSeconds(5));
            lock (_gate)
            {
                if (_session != current)
                {
                    return false;
                }

                // A Recording Started Meanwhile Keeps Its Session, Upgrades Wait
                if (isUpgrade && _manual?.Session == current)
                {
                    s_logger.Info(
                        $"Stayed On {current.Encoder.Name}, A Recording Runs Until Stopped"
                    );
                    return false;
                }

                // A Length Or Volume Changed Meanwhile Carries Over
                standby.Adjust(current.Options);
                _session = standby;
                Encoder = standby.Encoder;
            }

            _ = WatchEndAsync(standby);
            _ = UpgradeAsync(standby);
            UpdateStatus(standby, null);
            FinishRecordingOf(current);
            await Task.Run(() => current.Shutdown());
            s_logger.Info($"Moved Recording From {current.Encoder.Name} To {standby.Encoder.Name}");
            return true;
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task ProbeWindowAsync(CaptureSession current)
    {
        // Only A Hung Encoder Loses To A Fresh Capture
        var encoders = VideoEncoders
            .Candidates()
            .Where(encoder => encoder == current.Encoder || !encoder.IsHardware);
        foreach (var encoder in encoders)
        {
            if (current.Stopping || current.HasEnded)
            {
                return;
            }

            var swapped = await TryStandbyAsync(
                current,
                encoder,
                async standby =>
                {
                    using (var waited = new CancellationTokenSource())
                    {
                        await Task.WhenAny(
                            standby.Video!.FirstSegment,
                            Task.Delay(s_windowProbe, waited.Token),
                            current.Ended
                        );
                        waited.Cancel();
                    }

                    // A Window That Just Moved Reaches The Old One Too
                    await Task.Delay(s_windowWait);
                    return standby.Video.FirstSegment.IsCompletedSuccessfully
                        && current.Video!.SinceLastSegment > s_stallTimeout;
                },
                isUpgrade: false,
                "Failed To Probe The Window With"
            );
            if (swapped)
            {
                return;
            }
        }

        s_logger.Debug("Kept The Capture Of A Still Window");
    }
}
