using System.Runtime.InteropServices;
using LetsClip.Data.ViewModels;

namespace LetsClip.Services.Capture;

public static unsafe partial class MonitorCatalog
{
    // DXGI And Display Configuration Constants
    private const uint SoftwareAdapter = 0x2;
    private const uint OnlyActivePaths = 0x2;
    private const int SourceNameRequest = 1;
    private const int TargetNameRequest = 2;
    private const int AdvancedColorRequest = 9;
    private static readonly Guid s_factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        // DXGI Knows What Duplication Captures, Display Config Knows Names
        var details = DisplayDetails();
        var monitors = new List<MonitorInfo>();
        ForEachHardwareAdapter(
            (adapter, adapterIndex, adapterDescription) =>
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    if (Call(adapter, 7, outputIndex, out var output) < 0)
                    {
                        break;
                    }

                    try
                    {
                        OutputDescription description;
                        _ = (
                            (delegate* unmanaged[Stdcall]<nint, OutputDescription*, int>)Method(
                                output,
                                7
                            )
                        )(output, &description);
                        var gdiName = new string(description.DeviceName).TrimEnd('\0');
                        if (description.AttachedToDesktop == 0)
                        {
                            continue;
                        }

                        details.TryGetValue(gdiName, out var detail);
                        var width = description.Right - description.Left;
                        var height = description.Bottom - description.Top;
                        monitors.Add(
                            new MonitorInfo(
                                detail?.DevicePath ?? gdiName,
                                string.IsNullOrWhiteSpace(detail?.FriendlyName)
                                    ? $"Display {monitors.Count + 1}"
                                    : detail.FriendlyName,
                                description.Left,
                                description.Top,
                                width,
                                height,
                                RotationOf(description.Rotation),
                                detail?.RefreshRate ?? 60,
                                description.Left == 0 && description.Top == 0,
                                detail?.IsHdr ?? false,
                                adapterIndex,
                                (int)outputIndex
                            )
                        );
                    }
                    finally
                    {
                        Release(output);
                    }
                }
            }
        );
        return [.. monitors.OrderBy(monitor => monitor.Left).ThenBy(monitor => monitor.Top)];
    }

    public static MonitorInfo? Find(IEnumerable<MonitorInfo> monitors, string? devicePath)
    {
        return monitors.FirstOrDefault(monitor =>
            string.Equals(monitor.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase)
        );
    }

    public static MonitorInfo? MonitorOf(IEnumerable<MonitorInfo> monitors, nint window)
    {
        // The Monitor Holding The Middle Of The Window
        if (!GetWindowRect(window, out var bounds))
        {
            return null;
        }

        var x = (bounds.Left + bounds.Right) / 2;
        var y = (bounds.Top + bounds.Bottom) / 2;
        return monitors.FirstOrDefault(monitor =>
            x >= monitor.Left
            && x < monitor.Left + monitor.Width
            && y >= monitor.Top
            && y < monitor.Top + monitor.Height
        );
    }

    public static (int Width, int Height)? ClientSizeOf(nint window)
    {
        // The Area The Window Draws, Without Its Frame
        return
            GetClientRect(window, out var bounds)
            && bounds.Right - bounds.Left > 1
            && bounds.Bottom - bounds.Top > 1
            ? (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)
            : null;
    }

    public static IReadOnlyList<int> AdapterVendors()
    {
        var vendors = new List<int>();
        ForEachHardwareAdapter((_, _, description) => vendors.Add((int)description.VendorId));
        return vendors;
    }

    private static void ForEachHardwareAdapter(Action<nint, int, AdapterDescription> visit)
    {
        // Hardware Adapters Only, In DXGI Order Like FFmpeg
        if (CreateDXGIFactory1(s_factoryId, out var factory) < 0)
        {
            return;
        }

        try
        {
            for (uint index = 0; Call(factory, 12, index, out var adapter) >= 0; index++)
            {
                try
                {
                    AdapterDescription description;
                    _ = (
                        (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)Method(
                            adapter,
                            10
                        )
                    )(adapter, &description);
                    if ((description.Flags & SoftwareAdapter) == 0)
                    {
                        visit(adapter, (int)index, description);
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
    }

    private static int RotationOf(int dxgiRotation)
    {
        // DXGI_MODE_ROTATION, The Duplicated Frame Arrives Unrotated
        return dxgiRotation switch
        {
            2 => 90,
            3 => 180,
            4 => 270,
            _ => 0,
        };
    }

    private sealed record DisplayDetail(
        string DevicePath,
        string FriendlyName,
        bool IsHdr,
        int RefreshRate
    );

    private static Dictionary<string, DisplayDetail> DisplayDetails()
    {
        var details = new Dictionary<string, DisplayDetail>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(OnlyActivePaths, out var pathCount, out var modeCount) != 0)
        {
            return details;
        }

        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        fixed (PathInfo* pathPointer = paths)
        fixed (ModeInfo* modePointer = modes)
        {
            if (
                QueryDisplayConfig(
                    OnlyActivePaths,
                    ref pathCount,
                    pathPointer,
                    ref modeCount,
                    modePointer,
                    0
                ) != 0
            )
            {
                return details;
            }
        }

        foreach (var path in paths.AsSpan(0, (int)pathCount))
        {
            var source = new SourceName
            {
                Type = SourceNameRequest,
                Size = (uint)sizeof(SourceName),
                AdapterLow = path.SourceAdapterLow,
                AdapterHigh = path.SourceAdapterHigh,
                Id = path.SourceId,
            };
            var target = new TargetName
            {
                Type = TargetNameRequest,
                Size = (uint)sizeof(TargetName),
                AdapterLow = path.TargetAdapterLow,
                AdapterHigh = path.TargetAdapterHigh,
                Id = path.TargetId,
            };
            var color = new AdvancedColor
            {
                Type = AdvancedColorRequest,
                Size = (uint)sizeof(AdvancedColor),
                AdapterLow = path.TargetAdapterLow,
                AdapterHigh = path.TargetAdapterHigh,
                Id = path.TargetId,
            };
            if (
                DisplayConfigGetDeviceInfo(&source) != 0
                || DisplayConfigGetDeviceInfo(&target) != 0
            )
            {
                continue;
            }

            // Advanced Color Enabled Without Plain Wide Gamut Means HDR
            var isHdr =
                DisplayConfigGetDeviceInfo(&color) == 0
                && (color.Value & 0x2) != 0
                && (color.Value & 0x4) == 0;
            // The Target's Own Fraction Rounded, 59.94 Hz Reads 60
            var hertz =
                path.RefreshDenominator == 0
                    ? 0
                    : (int)Math.Round(path.RefreshNumerator / (double)path.RefreshDenominator);
            var gdiName = new string(source.ViewGdiDeviceName).TrimEnd('\0');
            details.TryAdd(
                gdiName,
                new DisplayDetail(
                    new string(target.MonitorDevicePath).TrimEnd('\0'),
                    new string(target.MonitorFriendlyDeviceName).TrimEnd('\0'),
                    isHdr,
                    hertz > 1 ? hertz : 60
                )
            );
        }

        return details;
    }

    private static nint Method(nint instance, int slot)
    {
        return (*(nint**)instance)[slot];
    }

    private static int Call(nint instance, int slot, uint index, out nint result)
    {
        // EnumAdapters1 And EnumOutputs Share One Shape
        nint value = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(instance, slot))(
            instance,
            index,
            &value
        );
        result = value;
        return hr;
    }

    private static void Release(nint instance)
    {
        if (instance != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Method(instance, 2))(instance);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        // DXGI_ADAPTER_DESC1
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDescription
    {
        // DXGI_OUTPUT_DESC
        public fixed char DeviceName[32];
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        // DISPLAYCONFIG_PATH_INFO
        public uint SourceAdapterLow;
        public int SourceAdapterHigh;
        public uint SourceId;
        public uint SourceModeIndex;
        public uint SourceStatus;
        public uint TargetAdapterLow;
        public int TargetAdapterHigh;
        public uint TargetId;
        public uint TargetModeIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatus;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        // DISPLAYCONFIG_MODE_INFO, Read Only Through Query
        [FieldOffset(0)]
        public uint InfoType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceName
    {
        // DISPLAYCONFIG_SOURCE_DEVICE_NAME
        public int Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
        public fixed char ViewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetName
    {
        // DISPLAYCONFIG_TARGET_DEVICE_NAME
        public int Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
        public uint Flags;
        public uint OutputTechnology;
        public ushort ManufactureId;
        public ushort ProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColor
    {
        // DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        public int Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out WindowBounds bounds);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out WindowBounds bounds);

    private struct WindowBounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(
        uint flags,
        out uint pathCount,
        out uint modeCount
    );

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        PathInfo* paths,
        ref uint modeCount,
        ModeInfo* modes,
        nint topology
    );

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(void* request);
}
