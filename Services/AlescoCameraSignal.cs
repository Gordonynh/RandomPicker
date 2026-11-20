using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 监听 Alesco 班级的「要用摄像头」信号，收到后立即让出摄像头。
/// </summary>
/// <remarks>
/// 教室大屏上的 Alesco 班级在拍照或取流之前，会把具名事件 <see cref="EventName"/> 置位。
/// 这里起一个后台线程等它：一旦置位，就关掉摄像头，并在
/// <see cref="CameraPicker.YieldDuration"/> 内不再打开（见 <see cref="CameraPicker.YieldToAlescoAsync"/>）。
/// <para/>
/// 事件是自动复位的，和 Alesco 一致：等到一次就自动清掉，不用手动复位。
/// 谁先启动谁创建，后启动的一方打开现成的；这里创建时放宽权限，
/// 让同一会话里以其他权限运行的 Alesco 也能打开并置位。
/// <para/>
/// 只在 Windows 上工作——具名事件和摄像头都是 Windows 专有的。
/// </remarks>
internal static class AlescoCameraSignal
{
    /// <summary>事件名。必须和 Alesco 班级一字不差。</summary>
    public const string EventName = @"Local\Alesco.CameraWanted";

    /// <summary>打不开事件时（例如权限不符），隔多久再试。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private static readonly object Sync = new();

    private static Thread? _thread;
    private static ManualResetEvent? _stop;

    /// <summary>开始监听。重复调用只起一个线程。</summary>
    public static void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Sync)
        {
            if (_thread is not null)
            {
                return;
            }

            var stop = new ManualResetEvent(false);
            var thread = new Thread(() => Run(stop))
            {
                IsBackground = true,
                Name = "RandomPicker.AlescoCameraSignal"
            };

            _stop = stop;
            _thread = thread;
            thread.Start();
        }
    }

    /// <summary>停止监听。插件停止时调用。</summary>
    public static void Stop()
    {
        Thread? thread;
        ManualResetEvent? stop;
        lock (Sync)
        {
            thread = _thread;
            stop = _stop;
            _thread = null;
            _stop = null;
        }

        if (thread is null || stop is null)
        {
            return;
        }

        stop.Set();

        // 线程只是在等两个句柄，置位后马上就会退出。
        // 万一没按时退出，就不释放 stop，免得它在线程里被用到已释放的句柄；后台线程不会拦住进程退出。
        if (thread.Join(TimeSpan.FromSeconds(2)))
        {
            stop.Dispose();
        }
    }

    private static void Run(ManualResetEvent stop)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        while (true)
        {
            WaitHandle? signal = null;
            try
            {
                signal = OpenOrCreate();
            }
            catch (Exception)
            {
                // 打不开就过一会儿再试，不影响抽人本身。
            }

            if (signal is not null)
            {
                using (signal)
                {
                    if (WaitLoop(stop, signal))
                    {
                        return;
                    }
                }
            }

            if (stop.WaitOne(RetryInterval))
            {
                return;
            }
        }
    }

    /// <summary>一直等到要求停止（返回 true）或句柄出错（返回 false，外层重新打开）。</summary>
    private static bool WaitLoop(ManualResetEvent stop, WaitHandle signal)
    {
        var handles = new WaitHandle[] { stop, signal };
        while (true)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch (Exception)
            {
                return false;
            }

            if (index == 0)
            {
                return true;
            }

            if (index == 1)
            {
                // 不在这里等关闭完成：截止时间在调用时就已生效，关闭在线程池上继续。
                _ = CameraPicker.YieldToAlescoAsync();
            }
        }
    }

    /// <summary>打开 Alesco 建好的事件；还没有就自己创建一个。</summary>
    [SupportedOSPlatform("windows")]
    private static WaitHandle? OpenOrCreate()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(EventName, out var existing))
                {
                    return existing;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 默认要「置位 + 等待」两项权限。Alesco 建的事件只放开等待时（例如它以管理员身份运行），
                // 退一步只要等待权限——这里本来也只需要等。
                return OpenForWaitOnly();
            }

            // 还没有：按放宽后的权限自己建。恰好被 Alesco 抢先建好时，拿到的就是那一个。
            var created = CreateWithSharedAccess();
            if (created is not null)
            {
                return created;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static WaitHandle? OpenForWaitOnly()
    {
        var raw = OpenEvent(Synchronize, false, EventName);
        return raw == IntPtr.Zero ? null : new NativeWaitHandle(raw);
    }

    /// <summary>
    /// 创建自动复位事件，权限：当前用户、SYSTEM、管理员组完全控制，其余已登录用户可置位和等待。
    /// </summary>
    /// <remarks>
    /// 默认权限取自创建者的令牌：ClassIsland 以管理员身份运行时，普通权限的 Alesco 打不开它。
    /// 名字带 <c>Local\</c> 前缀，本身就只在当前会话内可见。
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static WaitHandle? CreateWithSharedAccess()
    {
        var descriptor = IntPtr.Zero;
        try
        {
            var user = WindowsIdentity.GetCurrent().User?.Value;
            var sddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)" +
                       (user is null ? string.Empty : $"(A;;GA;;;{user})") +
                       $"(A;;0x{Synchronize | EventModifyState:X8};;;AU)";

            // 转换失败就用默认权限，至少同一用户的 Alesco 还能打开。
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SddlRevision1, out descriptor,
                    IntPtr.Zero))
            {
                descriptor = IntPtr.Zero;
            }

            var attributes = new SecurityAttributes
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                lpSecurityDescriptor = descriptor,
                bInheritHandle = 0
            };

            // flags = 0：自动复位、初始未置位，和 Alesco 一致。只要等待权限，已存在时也容易打开。
            var raw = CreateEventEx(ref attributes, EventName, 0, Synchronize);
            return raw == IntPtr.Zero ? null : new NativeWaitHandle(raw);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
            {
                LocalFree(descriptor);
            }
        }
    }

    /// <summary>把原生事件句柄包成 <see cref="WaitHandle"/>，好和停止信号一起等。</summary>
    private sealed class NativeWaitHandle : WaitHandle
    {
        public NativeWaitHandle(IntPtr handle)
        {
            SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        }
    }

    #region Win32

    private const uint Synchronize = 0x00100000;
    private const uint EventModifyState = 0x0002;
    private const uint SddlRevision1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateEventExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventEx(ref SecurityAttributes attributes, string name, uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", EntryPoint = "OpenEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
        out IntPtr descriptor, IntPtr descriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    #endregion
}
