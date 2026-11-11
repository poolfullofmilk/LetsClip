using System.Runtime.InteropServices;
using LetsClip.Data.Helpers;
using Forms = System.Windows.Forms;

namespace LetsClip.Desktop.Interop;

public sealed partial class WindowMessages : Forms.NativeWindow, IDisposable
{
    // Window Messages And Flags
    private const int HotkeyMessage = 0x0312;
    private const int PowerBroadcast = 0x0218;
    private const int PowerSettingChange = 0x8013;
    private const uint NoRepeat = 0x4000;
    private const int HotkeyId = 1;
    private static readonly Guid s_displayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly nint _powerRegistration;
    private bool _hotkeyRegistered;

    public WindowMessages()
    {
        // A Plain Hidden Window, A WPF One Costs 50 MB
        CreateHandle(new Forms.CreateParams());
        _powerRegistration = RegisterPowerSettingNotification(Handle, s_displayState, 0);
    }

    public event Action? HotkeyPressed;

    public event Action<bool>? DisplayPowerChanged;

    public bool RegisterHotkey(Hotkey hotkey)
    {
        // Held Keys Fire Once, The Combination Ours While Registered
        UnregisterHotkey();
        _hotkeyRegistered = RegisterHotKey(
            Handle,
            HotkeyId,
            (uint)hotkey.Modifiers | NoRepeat,
            (uint)hotkey.VirtualKey
        );
        return _hotkeyRegistered;
    }

    public void UnregisterHotkey()
    {
        if (_hotkeyRegistered)
        {
            _ = UnregisterHotKey(Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == HotkeyMessage && message.WParam == HotkeyId)
        {
            HotkeyPressed?.Invoke();
        }
        else if (
            message.Msg == PowerBroadcast
            && message.WParam == PowerSettingChange
            && message.LParam != 0
        )
        {
            // Data 0 Is Off, 1 On And 2 Dimmed
            var setting = Marshal.PtrToStructure<PowerSetting>(message.LParam);
            if (setting.Setting == s_displayState)
            {
                DisplayPowerChanged?.Invoke(setting.Data != 0);
            }
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        UnregisterHotkey();
        if (_powerRegistration != 0)
        {
            _ = UnregisterPowerSettingNotification(_powerRegistration);
        }

        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerSetting
    {
        // POWERBROADCAST_SETTING With Its One Byte Of Data
        public Guid Setting;
        public uint DataLength;
        public byte Data;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll")]
    private static partial nint RegisterPowerSettingNotification(
        nint recipient,
        in Guid setting,
        int flags
    );

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterPowerSettingNotification(nint handle);
}
