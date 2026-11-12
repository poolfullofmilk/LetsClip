namespace LetsClip.Data.ViewModels;

public sealed record PacketMap(
    double[] Times,
    long[] Sizes,
    int[] Keys,
    double AudioBytesPerSecond,
    double AudioPacketsPerSecond
);
