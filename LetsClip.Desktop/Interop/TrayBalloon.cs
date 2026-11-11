using System.Reflection;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace LetsClip.Desktop.Interop;

public static unsafe partial class TrayBalloon
{
    private const uint ModifyIcon = 0x1;
    private const uint ShowsInfo = 0x10;
    private const uint NoSound = 0x10;
    private const uint WarningSign = 0x2;

    // Ponytail: Private NotifyIcon Fields, May Break On A .NET Update
    private static readonly FieldInfo? s_window = typeof(Forms.NotifyIcon).GetField(
        "_window",
        BindingFlags.NonPublic | BindingFlags.Instance
    );
    private static readonly FieldInfo? s_id = typeof(Forms.NotifyIcon).GetField(
        "_id",
        BindingFlags.NonPublic | BindingFlags.Instance
    );

    public static void ShowQuiet(
        Forms.NotifyIcon icon,
        int milliseconds,
        string title,
        string text,
        bool isWarning = false
    )
    {
        var sign = isWarning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.None;
        // Without The Fields It Shows With The Windows Sound Instead
        if (
            s_window?.GetValue(icon) is not Forms.NativeWindow { Handle: not 0 } window
            || s_id?.GetValue(icon) is not uint id
        )
        {
            icon.ShowBalloonTip(milliseconds, title, text, sign);
            return;
        }

        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = window.Handle,
            Id = id,
            Flags = ShowsInfo,
            Timeout = (uint)milliseconds,
            InfoFlags = NoSound | (isWarning ? WarningSign : 0),
        };
        Copy(text, data.Info, 256);
        Copy(title, data.InfoTitle, 64);
        if (!ShellNotifyIcon(ModifyIcon, &data))
        {
            icon.ShowBalloonTip(milliseconds, title, text, sign);
        }
    }

    private static void Copy(string text, char* target, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(target, capacity));
        target[length] = '\0';
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        // NOTIFYICONDATAW, Only The Balloon Parts Are Filled
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Timeout;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint message, NotifyIconData* data);
}
