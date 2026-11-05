using System.Runtime.InteropServices;

namespace SteamCardCollector.App;

static class DropBalloon
{
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifInfo = 0x10;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NiifInfo = 1;

    public static void Show(IntPtr window, string text)
    {
        try { ShowCore(window, text); }
        catch (Exception) { }
    }

    public static void Hide(IntPtr window)
    {
        try
        {
            var data = Create(window, "");
            Shell_NotifyIcon(NimDelete, ref data);
        }
        catch (Exception) { }
    }

    private static void ShowCore(IntPtr window, string text)
    {
        if (window == IntPtr.Zero) return;
        var data = Create(window, text);
        Shell_NotifyIcon(NimDelete, ref data);
        Shell_NotifyIcon(NimAdd, ref data);
        Shell_NotifyIcon(NimModify, ref data);
    }

    private static NOTIFYICONDATA Create(IntPtr window, string text)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = window,
            uID = 1,
            uFlags = NifInfo | NifIcon | NifTip,
            hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32516)),
            szTip = "Steam Card Collector",
            szInfo = Trim(text, 240),
            szInfoTitle = SteamCardCollector.Core.Strings.CardDroppedTitle,
            dwInfoFlags = NiifInfo
        };
        return data;
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr icon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }
}
