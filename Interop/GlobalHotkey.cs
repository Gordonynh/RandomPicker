using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassIsland.RandomPicker.Interop;

/// <summary>
/// 基于 Win32 全局热键的辅助类。用一个「仅消息」窗口接收 <c>WM_HOTKEY</c>，
/// 这样即使悬浮窗被隐藏、没有焦点，快捷键也能把窗口叫回来。
/// </summary>
/// <remarks>
/// ClassIsland 只跑在 Windows 上，这里直接 P/Invoke <c>user32.dll</c>。
/// <b>窗口类注册、窗口创建、热键注册和消息泵必须在同一个线程</b>——
/// <c>WM_HOTKEY</c> 被投递到创建窗口的那个线程的消息队列，
/// 如果窗口在宿主的服务线程上创建、又在另一个线程 <c>GetMessage</c>，
/// 热键消息永远到不了泵里。所以整个生命周期都搬进后台线程：
/// 构造函数只存参数，线程里完成注册和循环。
/// </remarks>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WmQuit = 0x0012;
    private const int HotkeyId = 1;

    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private readonly Action _onHotkey;
    private readonly Thread _thread;

    private IntPtr _hwnd;      // 只在后台线程上访问
    private IntPtr _classAtom; // 只在后台线程上访问
    private WndProc _wndProc;  // 必须保活，否则被 GC 后回调会崩
    private volatile bool _disposed;

    public GlobalHotkey(uint modifiers, uint virtualKey, Action onHotkey)
    {
        _modifiers = modifiers;
        _virtualKey = virtualKey;
        _onHotkey = onHotkey;
        _wndProc = WndProcCallback;

        _thread = new Thread(Run) { IsBackground = true, Name = "RandomPickerHotkey" };
        _thread.Start();
    }

    private void Run()
    {
        var className = "RandomPickerHotkey_" + Guid.NewGuid().ToString("N");

        var wc = new WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = GetModuleHandle(null),
            LpszClassName = className
        };

        _classAtom = RegisterClassEx(ref wc);
        if (_classAtom == IntPtr.Zero)
        {
            return; // 注册失败就静默失效，不拦宿主启动
        }

        _hwnd = CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero,
            IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            UnregisterClass(_classAtom, GetModuleHandle(null));
            _classAtom = IntPtr.Zero;
            return;
        }

        if (!RegisterHotKey(_hwnd, HotkeyId, _modifiers, _virtualKey))
        {
            // 热键被别的程序占用了：静默失效——不影响其它功能。
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            UnregisterClass(_classAtom, GetModuleHandle(null));
            _classAtom = IntPtr.Zero;
            return;
        }

        while (!_disposed)
        {
            // 仅接收本窗口的消息；WM_QUIT 时 GetMessage 返回 0，循环退出。
            if (GetMessage(out var msg, _hwnd, 0, 0) == 0)
            {
                break;
            }

            if (msg.Message == WmHotkey && msg.WParam.ToInt32() == HotkeyId)
            {
                _onHotkey();
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // 清理只能在创建线程上做（DestroyWindow 的文档要求）。
        UnregisterHotKey(_hwnd, HotkeyId);
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        UnregisterClass(_classAtom, GetModuleHandle(null));
        _classAtom = IntPtr.Zero;
    }

    private IntPtr WndProcCallback(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProc(hWnd, msg, wParam, lParam);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // PostMessage 跨线程安全：让后台线程的 GetMessage 拿到 WM_QUIT，
        // 循环退出后由线程自己做注销和销毁。
        PostMessage(_hwnd, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(500);
    }

    #region P/Invoke

    private const int WmHotkey = 0x0312;
    private const string User32 = "user32.dll";

    private static readonly IntPtr HwndMessage = new(-3);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
        public IntPtr HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr UnregisterClass(IntPtr hClass, IntPtr hInstance);

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport(User32, SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport(User32, SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport(User32, SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport(User32)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32)]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport(User32)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport(User32)]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport(User32)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    #endregion
}
