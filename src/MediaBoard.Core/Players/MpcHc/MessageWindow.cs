using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace MediaBoard.Core.Players.MpcHc;

/// <summary>
/// A message-only window (parent <c>HWND_MESSAGE</c>) on its own STA thread with its own message loop, which
/// MPC-HC sends its WM_COPYDATA notifications to.
/// </summary>
/// <remarks>
/// <see cref="CopyDataReceived"/> is raised on the window's thread, after the WM_COPYDATA call has returned to
/// MPC-HC (the payload is copied and dispatched through a posted message), so a handler can send commands back
/// without re-entering MPC-HC while it is still waiting on its own send.
/// </remarks>
public sealed unsafe partial class MessageWindow : IMpcTransport
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_DISPATCH = 0x8000 + 1; // WM_APP + 1
    private const nint HWND_MESSAGE = -3;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SendTimeoutMs = 5000;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private const string ClassName = "MediaBoard.MpcHcHost";

    private static readonly ConcurrentDictionary<nint, MessageWindow> Windows = new();
    private static readonly Lock ClassGate = new();
    private static nint _classNamePtr;

    private readonly Thread _thread;
    private readonly ConcurrentQueue<(uint Cmd, string Payload)> _inbox = new();
    private ExceptionDispatchInfo? _startError;
    private nint _handle;
    private int _disposed;

    /// <summary>Creates the window and its thread; returns once the window exists.</summary>
    /// <exception cref="Win32Exception">The window could not be created.</exception>
    public MessageWindow()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "MPC-HC messages" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _startError?.Throw();
    }

    /// <summary>The window's handle, passed to MPC-HC as <c>/slave &lt;hwnd&gt;</c>.</summary>
    public nint Handle => _handle;

    /// <summary>A WM_COPYDATA message: <c>dwData</c> and the payload up to its null terminator.</summary>
    public event Action<uint, string>? CopyDataReceived;

    nint IMpcTransport.HostHandle => Handle;

    event Action<uint, string>? IMpcTransport.Received
    {
        add => CopyDataReceived += value;
        remove => CopyDataReceived -= value;
    }

    /// <summary>
    /// Sends WM_COPYDATA with a null-terminated UTF-16 payload. Any thread may call it. Gives up after 5 s, or at
    /// once if the target is hung or gone, so a frozen player cannot freeze the caller.
    /// </summary>
    public void Send(nint target, uint cmd, string payload)
    {
        if (Volatile.Read(ref _disposed) == 1) return;

        var buffer = Marshal.StringToHGlobalUni(payload);
        try
        {
            var data = new COPYDATASTRUCT
            {
                dwData = cmd,
                cbData = (uint)((payload.Length + 1) * sizeof(char)),
                lpData = buffer,
            };
            SendMessageTimeoutW(target, WM_COPYDATA, _handle, (nint)(&data), SMTO_ABORTIFHUNG, SendTimeoutMs, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Destroys the window and ends its thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_handle != 0) PostMessageW(_handle, WM_CLOSE, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run(ManualResetEventSlim ready)
    {
        try
        {
            var className = RegisterClass();
            var hwnd = CreateWindowExW(0, className, 0, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, GetModuleHandleW(0), 0);
            if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            Windows[hwnd] = this;
            _handle = hwnd;
        }
        catch (Exception e)
        {
            _startError = ExceptionDispatchInfo.Capture(e);
            ready.Set();
            return;
        }

        ready.Set();
        try
        {
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                DispatchMessageW(in msg);
            }
        }
        finally
        {
            Windows.TryRemove(_handle, out _);
        }
    }

    private static nint RegisterClass()
    {
        lock (ClassGate)
        {
            if (_classNamePtr != 0) return _classNamePtr;

            var name = Marshal.StringToHGlobalUni(ClassName); // kept for the life of the process, like the class
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = GetModuleHandleW(0),
                lpszClassName = name,
            };
            if (RegisterClassExW(in wc) == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                {
                    Marshal.FreeHGlobal(name);
                    throw new Win32Exception(error);
                }
            }

            _classNamePtr = name;
            return name;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (Windows.TryGetValue(hwnd, out var window))
            {
                switch (msg)
                {
                    case WM_COPYDATA:
                        window.Receive((COPYDATASTRUCT*)lParam);
                        return 1;
                    case WM_DISPATCH:
                        window.DispatchInbox();
                        return 0;
                    case WM_DESTROY:
                        PostQuitMessage(0);
                        return 0;
                }
            }
        }
        catch (Exception e)
        {
            // An exception must not unwind into native code: it would end the process.
            Trace.TraceError($"MPC-HC message window: {e}");
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Receive(COPYDATASTRUCT* data)
    {
        var payload = data->lpData == 0 || data->cbData < sizeof(char)
            ? ""
            : new string((char*)data->lpData, 0, (int)(data->cbData / sizeof(char)));
        var end = payload.IndexOf('\0');
        if (end >= 0) payload = payload[..end];

        _inbox.Enqueue(((uint)data->dwData, payload));
        PostMessageW(_handle, WM_DISPATCH, 0, 0);
    }

    private void DispatchInbox()
    {
        while (_inbox.TryDequeue(out var message))
        {
            try
            {
                CopyDataReceived?.Invoke(message.Cmd, message.Payload);
            }
            catch (Exception e)
            {
                Trace.TraceError($"MPC-HC message handler failed: {e}");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public nuint dwData;
        public uint cbData;
        public nint lpData;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(in WNDCLASSEXW wc);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(
        uint exStyle, nint className, nint windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out MSG msg, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in MSG msg);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageTimeoutW(
        nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint moduleName);
}
