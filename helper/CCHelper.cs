// computer control helper —— 常驻进程，stdin/stdout 一行一个 JSON。
// 能力：截图（可标注点击点/网格/放大镜）、鼠标、键盘、UIA 元素树、全局输入钩子、观看录制。
// 设计原则：只做原子动作，怎么判定交给上层 JS。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ComputerControl
{
    internal static class Native
    {
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int WM_QUIT = 0x0012;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScan(char ch);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetKeyNameText(int lParam, StringBuilder text, int count);
        [DllImport("user32.dll")] public static extern IntPtr GetMessageExtraInfo();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    internal static class InputLog
    {
        public static readonly object Sync = new object();
        public static readonly List<Dictionary<string, object>> Ring = new List<Dictionary<string, object>>();
        public static readonly List<Dictionary<string, object>> Session = new List<Dictionary<string, object>>();
        public static volatile bool Learning;
        public static DateTime SessionStart = DateTime.Now;
        /** 最近一次「真人」点击/滚轮/按键/移动的时间（注入事件不算）。 */
        public static volatile int LastUserTick;

        private static System.Threading.Timer _moveTimer;
        private static int _moveX;
        private static int _moveY;
        private static bool _moveValid;
        private static readonly List<KeyValuePair<int, int>> _shake = new List<KeyValuePair<int, int>>();
        private static int _shakeLastRaiseMs;

        /** 左右来回甩鼠标：1 秒内方向反转 ≥4 次且跨度 ≥120px，就发一次暂停事件。 */
        private static void FeedShake(int x, int t)
        {
            _shake.Add(new KeyValuePair<int, int>(x, t));
            while (_shake.Count > 0 && t - _shake[0].Value > 1000) _shake.RemoveAt(0);
            if (_shake.Count < 6) return;
            if (t - _shakeLastRaiseMs < 2500) return;

            int reversals = 0;
            int dir = 0;
            int minX = x, maxX = x;
            for (int i = 1; i < _shake.Count; i++)
            {
                int dx = _shake[i].Key - _shake[i - 1].Key;
                if (dx < -6) { if (dir > 0) reversals++; dir = -1; }
                else if (dx > 6) { if (dir < 0) reversals++; dir = 1; }
                if (_shake[i].Key < minX) minX = _shake[i].Key;
                if (_shake[i].Key > maxX) maxX = _shake[i].Key;
            }
            if (reversals >= 4 && maxX - minX >= 120)
            {
                _shakeLastRaiseMs = t;
                _shake.Clear();
                Overlay.Raise("overlay.shake");
            }
        }

        /** 我们自己移动光标后登记一下，免得被当成用户在动鼠标。 */
        public static void NoteCursorMove(int x, int y)
        {
            _moveX = x;
            _moveY = y;
            _moveValid = true;
        }

        /** 每 45ms 看一眼光标；位置变了又没登记，就认为是真人在挪鼠标。 */
        private static void StartMoveWatch()
        {
            if (_moveTimer != null) return;
            POINT p;
            if (Native.GetCursorPos(out p)) NoteCursorMove(p.X, p.Y);
            _moveTimer = new System.Threading.Timer(delegate
            {
                try
                {
                    POINT cur;
                    if (!Native.GetCursorPos(out cur)) return;
                    if (!_moveValid) { NoteCursorMove(cur.X, cur.Y); return; }
                    if (cur.X != _moveX || cur.Y != _moveY)
                    {
                        LastUserTick = Environment.TickCount;
                        _moveX = cur.X;
                        _moveY = cur.Y;
                        FeedShake(cur.X, Environment.TickCount);
                    }
                }
                catch { }
            }, null, 45, 45);
        }

        private static IntPtr _mouseHook;
        private static IntPtr _keyHook;
        private static Thread _thread;
        private static uint _threadId;
        private static readonly IntPtr Marker = new IntPtr(0x1C0DE1);
        private static readonly Dictionary<IntPtr, string> ExeCache = new Dictionary<IntPtr, string>();
        private static readonly Dictionary<uint, string> NameCache = new Dictionary<uint, string>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static readonly string[] ModifierKeys = new string[] { "ctrl", "shift", "alt", "win" };

        /** 距离最近一次真人输入多久；从没输入过就算很久。 */
        public static int UserIdleMs
        {
            get
            {
                int t = LastUserTick;
                if (t == 0) return int.MaxValue;
                int idle = unchecked(Environment.TickCount - t);
                return idle < 0 ? 0 : idle;
            }
        }

        public static void Install()
        {
            if (_thread != null) return;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Thread.Sleep(80);
            StartMoveWatch();
        }

        public static void Uninstall()
        {
            if (_thread == null) return;
            try { Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            try { if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook); } catch { }
            try { if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook); } catch { }
            _mouseHook = IntPtr.Zero;
            _keyHook = IntPtr.Zero;
            _thread = null;
        }

        private static void Run()
        {
            _threadId = Native.GetCurrentThreadId();
            IntPtr module = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, MouseProc, module, 0);
            _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, KeyProc, module, 0);
            MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }

        private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if (data.dwExtraInfo != Marker)
                    {
                        int msg = wParam.ToInt32();
                        if (msg == 0x0201) Record("mouse", "click", "left", data.pt.X, data.pt.Y, 0);
                        else if (msg == 0x0204) Record("mouse", "click", "right", data.pt.X, data.pt.Y, 0);
                        else if (msg == 0x0207) Record("mouse", "click", "middle", data.pt.X, data.pt.Y, 0);
                        else if (msg == 0x020A) Record("mouse", "wheel", null, data.pt.X, data.pt.Y, (short)((data.mouseData >> 16) & 0xFFFF));
                        LastUserTick = Environment.TickCount;
                    }
                }
                catch { }
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private static IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    // 0x10 = LLKHF_INJECTED，别的程序注入的按键不算用户操作
                    if (data.dwExtraInfo != Marker && (data.flags & 0x10) == 0)
                    {
                        int msg = wParam.ToInt32();
                        if (msg == 0x0100 || msg == 0x0104)
                            Record("key", "down", null, -1, -1, 0, data.vkCode);
                        LastUserTick = Environment.TickCount;
                    }
                }
                catch { }
            }
            return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private static void Record(string kind, string action, string button, int x, int y, int data)
        {
            Record(kind, action, button, x, y, data, 0);
        }

        private static void Record(string kind, string action, string button, int x, int y, int data, uint vk)
        {
            Dictionary<string, object> ev = new Dictionary<string, object>();
            DateTime now = DateTime.Now;
            ev["t"] = now.ToString("HH:mm:ss.fff");
            ev["wall"] = now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            ev["ms"] = Clock.ElapsedMilliseconds;
            ev["kind"] = kind;
            ev["action"] = action;
            if (button != null) ev["button"] = button;
            if (x >= 0) { ev["x"] = x; ev["y"] = y; }
            if (kind == "wheel" || (kind == "mouse" && action == "wheel")) ev["wheel"] = data;
            if (vk != 0)
            {
                ev["vk"] = (int)vk;
                string name = KeyName(vk);
                if (name != null) ev["key"] = name;
                string mods = CurrentModifiers();
                if (mods.Length > 0) ev["mods"] = mods;
            }
            IntPtr fg = Native.GetForegroundWindow();
            ev["hwnd"] = fg.ToInt64();
            ev["title"] = WindowTitle(fg);
            string exe = ExeOf(fg);
            if (exe != null) ev["exe"] = exe;

            lock (Sync)
            {
                Ring.Add(ev);
                if (Ring.Count > 400) Ring.RemoveAt(0);
                if (Learning)
                {
                    Session.Add(ev);
                    if (Session.Count > 20000) Session.RemoveAt(0);
                }
            }
        }

        public static List<Dictionary<string, object>> Tail(int count)
        {
            lock (Sync)
            {
                int start = Math.Max(0, Ring.Count - count);
                return new List<Dictionary<string, object>>(Ring.GetRange(start, Ring.Count - start));
            }
        }

        public static List<Dictionary<string, object>> SessionCopy()
        {
            lock (Sync) { return new List<Dictionary<string, object>>(Session); }
        }

        public static void ClearSession()
        {
            lock (Sync) { Session.Clear(); }
        }

        private static string CurrentModifiers()
        {
            List<string> mods = new List<string>();
            try
            {
                if ((NativeGetAsync(0x11) & 0x8000) != 0) mods.Add("ctrl");
                if ((NativeGetAsync(0x10) & 0x8000) != 0) mods.Add("shift");
                if ((NativeGetAsync(0x12) & 0x8000) != 0) mods.Add("alt");
                if (((NativeGetAsync(0x5B) & 0x8000) != 0) || ((NativeGetAsync(0x5C) & 0x8000) != 0)) mods.Add("win");
            }
            catch { }
            return string.Join("+", mods.ToArray());
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        private static short NativeGetAsync(int vk) { return GetAsyncKeyState(vk); }

        private static string KeyName(uint vk)
        {
            lock (NameCache)
            {
                string cached;
                if (NameCache.TryGetValue(vk, out cached)) return cached;
            }
            string name = null;
            try
            {
                uint scan = Native.MapVirtualKey(vk, 0);
                int lParam = (int)(scan << 16);
                if ((vk >= 0x21 && vk <= 0x2E) || (vk >= 0x70 && vk <= 0x87)) lParam |= (1 << 24);
                StringBuilder sb = new StringBuilder(64);
                int n = Native.GetKeyNameText(lParam, sb, sb.Capacity);
                if (n > 0) name = sb.ToString().ToLowerInvariant();
            }
            catch { }
            if (name == null)
            {
                if (vk >= 0x30 && vk <= 0x39) name = ((char)vk).ToString();
                else if (vk >= 0x41 && vk <= 0x5A) name = ((char)vk).ToString().ToLowerInvariant();
            }
            if (name != null) lock (NameCache) { NameCache[vk] = name; }
            return name;
        }

        public static string WindowTitle(IntPtr hwnd)
        {
            try
            {
                int len = Native.GetWindowTextLength(hwnd);
                if (len <= 0) return "";
                StringBuilder sb = new StringBuilder(len + 2);
                Native.GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        public static string ExeOf(IntPtr hwnd)
        {
            try
            {
                lock (ExeCache) { string c; if (ExeCache.TryGetValue(hwnd, out c)) return c; }
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return null;
                string exe = null;
                try { using (Process p = Process.GetProcessById((int)pid)) exe = p.ProcessName + ".exe"; }
                catch { }
                lock (ExeCache) { if (ExeCache.Count > 300) ExeCache.Clear(); ExeCache[hwnd] = exe; }
                return exe;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 屏幕上的「灵动岛」悬浮窗：显示当前状态，可拖动、可锁定，点停止会发事件给上层。
    /// 独立 STA 线程 + 无激活窗口，尽量不影响正在操作的程序，也尽量不进截图。
    /// </summary>
    internal static class Overlay
    {
        private static OverlayForm _form;
        private static Thread _thread;
        private static readonly object Sync = new object();
        private static string _lastError;
        public static Action<string, Dictionary<string, object>> Event;

        private static void Log(string text)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-overlay.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine);
            }
            catch { }
        }

        public static string LastError { get { return _lastError; } }
        public static bool Created { get { return _form != null; } }
        public static string Bounds
        {
            get
            {
                try
                {
                    OverlayForm form = _form;
                    if (form == null) return "";
                    return string.Format("{0},{1} {2}x{3}", form.Left, form.Top, form.Width, form.Height);
                }
                catch { return ""; }
            }
        }
        public static bool Visible
        {
            get
            {
                try
                {
                    OverlayForm form = _form;
                    return form != null && form.Visible;
                }
                catch { return false; }
            }
        }

        public static string StatusText
        {
            get
            {
                try
                {
                    OverlayForm form = _form;
                    return form == null ? "" : form.StatusText;
                }
                catch { return ""; }
            }
        }

        public static string ChatText
        {
            get
            {
                try
                {
                    OverlayForm form = _form;
                    return form == null ? "" : form.ChatText;
                }
                catch { return ""; }
            }
        }

        public static void Ensure()
        {
            if (_thread != null) return;
            lock (Sync)
            {
                if (_thread != null) return;
                Log("ensure start");
                ManualResetEvent ready = new ManualResetEvent(false);
                _thread = new Thread(delegate()
                {
                    try
                    {
                        Application.EnableVisualStyles();
                        _form = new OverlayForm();
                        IntPtr handle = _form.Handle;
                        Log("form created hwnd=" + handle);
                        ready.Set();
                        Application.Run(_form);
                        Log("message loop exited");
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.ToString();
                        Log("form failed: " + ex);
                        ready.Set();
                    }
                });
                _thread.IsBackground = true;
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.WaitOne(4000);
                Thread.Sleep(60);
                Log("ensure done created=" + (_form != null));
            }
        }

        public static void Configure(bool enabled, string position, int x, int y, bool locked, int width, double opacity)
        {
            Ensure();
            Run(delegate { if (_form != null) _form.Configure(enabled, position, x, y, locked, width, opacity); });
        }

        public static void State(string mode, string label, string action, int delayHideMs, bool userPriority, bool paused)
        {
            Ensure();
            Run(delegate { if (_form != null) _form.UpdateState(mode, label, action, delayHideMs, userPriority, paused); });
        }

        public static void Preview(int durationMs)
        {
            Ensure();
            Run(delegate { if (_form != null) _form.Preview(durationMs); });
        }

        /** 临时状态提示（用户抢鼠标时显示「让开中」），到点自动恢复。 */
        public static void Alert(string text, int durationMs)
        {
            Ensure();
            Run(delegate { if (_form != null) _form.ShowAlert(text, durationMs); });
        }

        /** 往观看抽屉的聊天历史里追加一行。 */
        public static void ChatLog(string line)
        {
            Ensure();
            Run(delegate { if (_form != null) _form.AppendChat(line); });
        }

        /** 打开/收起观看抽屉（给设置页或自检用）。 */
        public static void ToggleDrawer()
        {
            Ensure();
            Run(delegate { if (_form != null) _form.ToggleDrawerPublic(); });
        }

        /** 控制动作的目标点被悬浮岛盖住时，先把悬浮岛藏起来，动作结束再回来。 */
        public static void DodgeIfCovered(int x, int y, int holdMs)
        {
            try
            {
                OverlayForm form = _form;
                if (form == null) return;
                form.BeginInvoke((Action)delegate { form.DodgeIfCovered(x, y, holdMs); });
            }
            catch { }
        }

        private static void Run(Action action)
        {
            try
            {
                OverlayForm form = _form;
                if (form == null) return;
                if (form.InvokeRequired) form.BeginInvoke(action);
                else action();
            }
            catch { }
        }

        internal static void Raise(string name)
        {
            Raise(name, null);
        }

        internal static void Raise(string name, Dictionary<string, object> data)
        {
            Action<string, Dictionary<string, object>> handler = Event;
            if (handler != null) handler(name, data);
        }
    }

    internal sealed class OverlayForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        private readonly Label _status;
        private readonly Label _focus;
        private readonly Label _stop;
        private readonly Label _pause;
        private readonly Label _drawer;
        private readonly Panel _drawerPanel;
        private readonly TextBox _chatLog;
        private readonly TextBox _chatInput;
        private readonly Label _chatSend;
        private bool _drawerOpen;
        private readonly LockGlyph _lock;
        private readonly Label _close;
        private readonly System.Windows.Forms.Timer _hideTimer;
        private readonly System.Windows.Forms.Timer _previewTimer;
        private readonly System.Windows.Forms.Timer _focusTimer;
        private readonly System.Windows.Forms.Timer _alertTimer;
        private string _alertText = "";
        private bool _locked;
        private bool _enabled = true;
        private string _mode = "idle";
        private string _label = "待机";
        private string _action = "";
        private bool _userPriority = true;
        private bool _paused;
        private string _position = "top-center";
        private int _customX;
        private int _customY;
        private bool _dragging;
        private bool _moved;
        private Point _dragStart;
        private Point _windowStart;
        private readonly System.Windows.Forms.Timer _animTimer;
        private readonly System.Windows.Forms.Timer _dodgeTimer;
        private readonly Stopwatch _animClock = new Stopwatch();
        private bool _opening;
        private bool _closing;
        private bool _dodged;
        private int _animCenterX;
        private int _animCenterY;
        private readonly Panel _gripLeft;
        private readonly Panel _gripRight;
        private bool _resizing;
        private bool _resizeFromLeft;
        private int _resizeStartX;
        private int _resizeStartWidth;
        private int _resizeCenterX;
        private int _fullWidth = 460;
        private double _baseOpacity = 0.85;
        private const int FullHeight = 62;

        public OverlayForm()
        {
            Text = "computer control";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(460, 62);
            BackColor = Color.FromArgb(20, 22, 26);
            DoubleBuffered = true;
            Font = new Font("Microsoft YaHei UI", 9f);

            _status = MakeText("待机", Color.White, ContentAlignment.MiddleLeft, true);
            _status.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            _status.AutoEllipsis = true;
            _focus = MakeText("", Color.FromArgb(175, 185, 200), ContentAlignment.MiddleLeft, true);
            _focus.Font = new Font("Microsoft YaHei UI", 8.5f);
            _focus.AutoEllipsis = true;
            _stop = MakeText("结束", Color.FromArgb(255, 128, 128), ContentAlignment.MiddleCenter);
            _pause = MakeText("暂停", Color.FromArgb(255, 196, 80), ContentAlignment.MiddleCenter);
            _pause.Cursor = Cursors.Hand;

            _drawer = MakeText("聊天", Color.FromArgb(150, 190, 255), ContentAlignment.MiddleCenter);
            _drawer.Cursor = Cursors.Hand;
            _drawer.Click += delegate { if (Injected()) return; ToggleDrawer(); };

            _drawerPanel = new Panel();
            _drawerPanel.BackColor = Color.FromArgb(24, 27, 32);
            _drawerPanel.Visible = false;
            Controls.Add(_drawerPanel);

            _chatLog = new TextBox();
            _chatLog.Multiline = true;
            _chatLog.ReadOnly = true;
            _chatLog.ScrollBars = ScrollBars.Vertical;
            _chatLog.BorderStyle = BorderStyle.None;
            _chatLog.BackColor = Color.FromArgb(16, 18, 22);
            _chatLog.ForeColor = Color.FromArgb(220, 228, 240);
            _chatLog.Font = new Font("Microsoft YaHei UI", 9f);
            _chatLog.WordWrap = true;
            _drawerPanel.Controls.Add(_chatLog);

            _chatInput = new TextBox();
            _chatInput.BorderStyle = BorderStyle.None;
            _chatInput.BackColor = Color.FromArgb(32, 36, 44);
            _chatInput.ForeColor = Color.White;
            _chatInput.Font = new Font("Microsoft YaHei UI", 9.5f);
            _chatInput.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendChat();
                }
            };
            _drawerPanel.Controls.Add(_chatInput);

            _chatSend = new Label();
            _chatSend.Text = "发送";
            _chatSend.TextAlign = ContentAlignment.MiddleCenter;
            _chatSend.BackColor = Color.FromArgb(77, 107, 254);
            _chatSend.ForeColor = Color.White;
            _chatSend.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _chatSend.Cursor = Cursors.Hand;
            _chatSend.Click += delegate { if (Injected()) return; SendChat(); };
            _drawerPanel.Controls.Add(_chatSend);

            _lock = new LockGlyph();
            Controls.Add(_lock);
            _close = MakeText("×", Color.FromArgb(170, 180, 195), ContentAlignment.MiddleCenter);
            _close.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            _close.Cursor = Cursors.Hand;

            _hit.Add(_stop);
            _hit.Add(_pause);
            _hit.Add(_drawer);
            _hit.Add(_lock);
            _hit.Add(_close);

            // 左右两条拖动条：开锁时可拖动调宽，中心对称
            _gripLeft = MakeGrip();
            _gripRight = MakeGrip();

            _stop.Cursor = Cursors.Hand;
            _stop.Click += delegate { if (Injected()) return; Overlay.Raise("overlay.stop"); };
            _pause.Click += delegate { if (Injected()) return; TogglePause(); };
            _lock.Click += delegate { if (Injected()) return; ToggleLock(); };
            _close.Click += delegate { if (Injected()) return; Overlay.Raise("overlay.close"); };

            _status.MouseDown += DragDown;
            _status.MouseMove += DragMove;
            _status.MouseUp += DragUp;
            _lock.MouseDown += DragDown;
            _lock.MouseMove += DragMove;
            _lock.MouseUp += DragUp;
            MouseDown += DragDown;
            MouseMove += DragMove;
            MouseUp += DragUp;

            _hideTimer = new System.Windows.Forms.Timer();
            _hideTimer.Interval = 2000;
            _hideTimer.Tick += delegate { _hideTimer.Stop(); HideIsland(true); };

            // 收拢/展开动画
            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 16;
            _animTimer.Tick += AnimTick;

            // 控制动作命中悬浮岛时临时藏起来，动作结束再回来
            _dodgeTimer = new System.Windows.Forms.Timer();
            _dodgeTimer.Interval = 700;
            _dodgeTimer.Tick += delegate
            {
                _dodgeTimer.Stop();
                _dodged = false;
                if (_enabled && _mode != "idle") ShowIslandNoAnim();
            };

            // 预览时临时允许截屏，方便在设置页里看清样式；结束后恢复「排除截屏」。
            _previewTimer = new System.Windows.Forms.Timer();
            _previewTimer.Tick += delegate
            {
                _previewTimer.Stop();
                SetCaptureVisible(false);
                if (_mode == "idle") HideIsland(true);
                else UpdateState(_mode, _label, _action, 2000, _userPriority, _paused);
            };

            // 临时状态提示：比如「让开中：检测到你在操作」，过几秒自动恢复
            _alertTimer = new System.Windows.Forms.Timer();
            _alertTimer.Interval = 2200;
            _alertTimer.Tick += delegate
            {
                _alertTimer.Stop();
                _alertText = "";
                UpdateStatusText();
            };

            // 第二行实时显示当前聚焦窗口，每秒刷新
            _focusTimer = new System.Windows.Forms.Timer();
            _focusTimer.Interval = 1200;
            _focusTimer.Tick += delegate { if (Visible) UpdateFocus(); };

            Region = RoundedRegion(Size, 23);
            ApplyLayout();
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetCaptureVisible(false);
        }

        private void SetCaptureVisible(bool allow)
        {
            try { SetWindowDisplayAffinity(Handle, allow ? 0u : 0x11u); }
            catch
            {
                if (allow) return;
                try { SetWindowDisplayAffinity(Handle, 0x01); } catch { }
            }
        }

        public void Preview(int durationMs)
        {
            _hideTimer.Stop();
            _status.Text = "预览 · computer control";
            Invalidate();
            ShowIsland();
            SetCaptureVisible(true);
            _previewTimer.Stop();
            _previewTimer.Interval = Math.Max(800, Math.Min(30000, durationMs));
            _previewTimer.Start();
        }

        private Label MakeText(string text, Color color, ContentAlignment align, bool passthrough = false)
        {
            Label label = passthrough ? (Label)new PassThroughLabel() : new Label();
            label.Text = text;
            label.ForeColor = color;
            // 用胶囊同色而不是透明：透明 Label 在分层不透明度下会在边缘露出一条接缝
            label.BackColor = BackColor;
            label.AutoSize = false;
            label.TextAlign = align;
            label.Font = new Font("Microsoft YaHei UI", 9f);
            label.Cursor = Cursors.SizeAll;
            Controls.Add(label);
            return label;
        }

        private void ApplyLayout()
        {
            _gripLeft.Bounds = new Rectangle(0, 6, 8, Math.Max(10, Height - 12));
            _gripRight.Bounds = new Rectangle(Width - 8, 6, 8, Math.Max(10, Height - 12));
            _gripLeft.Visible = !_locked && !_drawerOpen;
            _gripRight.Visible = !_locked && !_drawerOpen;

            bool watchMode = _mode == "watch";
            _drawer.Visible = watchMode;
            if (_locked)
            {
                // 锁定：藏掉 ×（不能关、不能拖），只留聊天、暂停、结束和锁
                _close.Visible = false;
                _lock.Bounds = new Rectangle(Width - 40, 12, 30, 38);
                _stop.Bounds = new Rectangle(Width - 96, 12, 50, 38);
                _pause.Bounds = new Rectangle(Width - 154, 12, 50, 38);
                _drawer.Bounds = new Rectangle(Width - 214, 12, 50, 38);
                _status.Bounds = new Rectangle(34, 4, Width - (watchMode ? 256 : 196), 28);
                _focus.Bounds = new Rectangle(34, 30, Width - (watchMode ? 256 : 196), 28);
                _status.Cursor = Cursors.Default;
            }
            else
            {
                _close.Visible = true;
                _close.Bounds = new Rectangle(Width - 34, 12, 26, 38);
                _lock.Bounds = new Rectangle(Width - 66, 12, 30, 38);
                _stop.Bounds = new Rectangle(Width - 118, 12, 50, 38);
                _pause.Bounds = new Rectangle(Width - 176, 12, 50, 38);
                _drawer.Bounds = new Rectangle(Width - 236, 12, 50, 38);
                _status.Bounds = new Rectangle(34, 4, Width - (watchMode ? 278 : 220), 28);
                _focus.Bounds = new Rectangle(34, 30, Width - (watchMode ? 278 : 220), 28);
                _status.Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color dot = _mode == "control"
                ? Color.FromArgb(77, 107, 254)
                : _mode == "watch"
                    ? Color.FromArgb(34, 197, 94)
                    : Color.FromArgb(148, 163, 184);
            using (SolidBrush brush = new SolidBrush(dot))
                e.Graphics.FillEllipse(brush, 14, Height / 2 - 5, 10, 10);
            using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255)))
                e.Graphics.DrawLine(pen, Width - 124, 12, Width - 124, Height - 12);
        }

        public void Configure(bool enabled, string position, int x, int y, bool locked, int width, double opacity)
        {
            _enabled = enabled;
            _position = string.IsNullOrEmpty(position) ? "top-center" : position;
            _customX = x;
            _customY = y;
            _locked = locked;
            _lock.Locked = _locked;
            _lock.Cursor = _locked ? Cursors.Default : Cursors.Hand;
            _baseOpacity = Math.Max(0.3, Math.Min(1.0, opacity <= 0 ? 0.85 : opacity));
            if (!_animTimer.Enabled) Opacity = _baseOpacity;

            int targetWidth = Math.Max(300, Math.Min(900, width <= 0 ? _fullWidth : width));
            if (targetWidth != _fullWidth)
            {
                int centerX = Left + Width / 2;
                _fullWidth = targetWidth;
                Size = new Size(_fullWidth, FullHeight);
                if (Visible)
                {
                    Left = centerX - _fullWidth / 2;
                    Region = RoundedRegion(Size, 23);
                }
            }
            ApplyLayout();
            if (!_enabled)
            {
                _hideTimer.Stop();
                HideIsland(false);
                return;
            }
            ApplyPosition();
            if (_mode != "idle") ShowIsland();
        }

        private void ApplyPosition()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int left;
            int top;
            if (_position == "custom")
            {
                left = _customX;
                top = _customY;
            }
            else if (_position == "top-left")
            {
                left = wa.X + 12;
                top = wa.Y + 8;
            }
            else if (_position == "top-right")
            {
                left = wa.Right - Width - 12;
                top = wa.Y + 8;
            }
            else
            {
                left = wa.X + (wa.Width - Width) / 2;
                top = wa.Y + 8;
            }
            Location = new Point(left, top);
        }

        public string StatusText
        {
            get { return _status.Text; }
        }

        public string ChatText
        {
            get { return _chatLog.Text ?? ""; }
        }

        public void UpdateState(string mode, string label, string action, int delayHideMs, bool userPriority, bool paused)
        {
            _mode = string.IsNullOrEmpty(mode) ? "idle" : mode;
            _label = string.IsNullOrEmpty(label) ? "待机" : label;
            _action = action ?? "";
            _userPriority = userPriority;
            _paused = paused;
            if (_mode != "watch" && _drawerOpen) SetDrawerOpen(false);
            ApplyPause();
            if (!_animTimer.Enabled) ApplyLayout();
            if (_mode == "idle") _focus.Text = "";
            else UpdateFocus();
            Invalidate();
            if (!_enabled) return;
            _hideTimer.Stop();
            if (_mode == "idle")
            {
                _hideTimer.Interval = Math.Max(200, Math.Min(60000, delayHideMs));
                _hideTimer.Start();
            }
            else
            {
                ShowIsland();
            }
        }

        private void UpdateStatusText()
        {
            if (!string.IsNullOrEmpty(_alertText))
            {
                _status.Text = _alertText;
                return;
            }
            if (_paused && _mode != "idle")
            {
                _status.Text = "已暂停 · 点「继续」恢复操控";
                return;
            }
            _status.Text = _mode == "idle"
                ? _label
                : _label + (string.IsNullOrEmpty(_action) ? "" : " · " + _action);
        }

        private void TogglePause()
        {
            _paused = !_paused;
            ApplyPause();
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["paused"] = _paused;
            Overlay.Raise("overlay.pause", data);
        }

        private void ApplyPause()
        {
            _pause.Text = _paused ? "继续" : "暂停";
            UpdateStatusText();
        }

        private void ToggleDrawer()
        {
            if (!_drawerOpen && _mode != "watch") return;
            SetDrawerOpen(!_drawerOpen);
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["open"] = _drawerOpen;
            Overlay.Raise("overlay.drawer", data);
        }

        public void ToggleDrawerPublic()
        {
            ToggleDrawer();
        }

        private void SetNoActivate(bool noActivate)
        {
            try
            {
                long style = Native.GetWindowLongPtr(Handle, -20).ToInt64();
                if (noActivate) style |= 0x08000000L;
                else style &= ~0x08000000L;
                Native.SetWindowLongPtr(Handle, -20, new IntPtr(style));
            }
            catch { }
        }

        private void SetDrawerOpen(bool open)
        {
            if (open == _drawerOpen) return;
            _drawerOpen = open;
            _hideTimer.Stop();
            if (open)
            {
                if (_mode != "watch") { _drawerOpen = false; return; }
                _animTimer.Stop();
                _opening = false;
                _closing = false;
                SetNoActivate(false);
                Height = 400;
                Region = RoundedRegion(Size, 23);
                _drawerPanel.Visible = true;
                _drawerPanel.Bounds = new Rectangle(10, 56, Width - 20, Height - 66);
                int w = _drawerPanel.Width;
                _chatLog.Bounds = new Rectangle(6, 6, w - 12, _drawerPanel.Height - 58);
                _chatInput.Bounds = new Rectangle(6, _drawerPanel.Height - 46, w - 118, 38);
                _chatSend.Bounds = new Rectangle(w - 106, _drawerPanel.Height - 46, 100, 38);
                _drawer.Text = "收起";
                ApplyLayout();
                Activate();
                _chatInput.Focus();
            }
            else
            {
                _drawerPanel.Visible = false;
                _drawer.Text = "聊天";
                Height = 62;
                Region = RoundedRegion(Size, 23);
                SetNoActivate(true);
                ApplyLayout();
                _hideTimer.Interval = 2000;
                _hideTimer.Start();
            }
        }

        private void SendChat()
        {
            string text = _chatInput.Text == null ? "" : _chatInput.Text.Trim();
            if (text.Length == 0) return;
            _chatInput.Text = "";
            AppendChat("你：" + text);
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["text"] = text;
            Overlay.Raise("overlay.chat", data);
        }

        public void AppendChat(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            try
            {
                string old = _chatLog.Text ?? "";
                string next = old.Length == 0 ? line : old + "\r\n" + line;
                if (next.Length > 8000) next = next.Substring(next.Length - 8000);
                _chatLog.Text = next;
                if (_chatLog.IsHandleCreated)
                {
                    _chatLog.SelectionStart = _chatLog.TextLength;
                    _chatLog.ScrollToCaret();
                }
            }
            catch { }
        }

        /** 临时改第一行文字（用户抢鼠标时显示「让开中」之类），到点自动恢复。 */
        public void ShowAlert(string text, int durationMs)
        {
            _alertText = text ?? "";
            UpdateStatusText();
            _alertTimer.Stop();
            _alertTimer.Interval = Math.Max(600, Math.Min(60000, durationMs));
            _alertTimer.Start();
        }

        private void ShowIsland()
        {
            if (!_enabled || _dodged) return;
            if (Visible)
            {
                if (_closing)
                {
                    _animTimer.Stop();
                    _closing = false;
                    FinishOpen();
                }
                TopMost = true;
                return;
            }
            if (_closing)
            {
                _animTimer.Stop();
                _closing = false;
            }
            HideControls(true);
            ApplyPosition();
            _animCenterX = Left + Width / 2;
            _animCenterY = Top + Height / 2;
            _opening = true;
            Opacity = 0;
            SetAnimatedBounds(_animCenterX, _animCenterY, 8, 8, 0);
            Show();
            TopMost = true;
            _animClock.Restart();
            _animTimer.Start();
        }

        private void ShowIslandNoAnim()
        {
            if (!_enabled || _dodged) return;
            HideControls(true);
            Size = new Size(_fullWidth, FullHeight);
            ApplyPosition();
            _animCenterX = Left + Width / 2;
            _animCenterY = Top + Height / 2;
            Region = RoundedRegion(Size, 23);
            Opacity = _baseOpacity;
            HideControls(false);
            ApplyLayout();
            Show();
            TopMost = true;
            UpdateFocus();
        }

        private void HideIsland(bool animate)
        {
            if (!Visible) return;
            if (!animate || _dodged)
            {
                _animTimer.Stop();
                _opening = false;
                _closing = false;
                base.Hide();
                Size = new Size(_fullWidth, FullHeight);
                Opacity = _baseOpacity;
                return;
            }
            if (_animTimer.Enabled) return;
            _closing = true;
            _opening = false;
            _animCenterX = Left + Width / 2;
            _animCenterY = Top + Height / 2;
            HideControls(true);
            _animClock.Restart();
            _animTimer.Start();
        }

        /** 收拢/展开动画：先两侧收成圆，再缩放淡出；打开反过来。 */
        private void AnimTick(object sender, EventArgs e)
        {
            double t = _animClock.Elapsed.TotalMilliseconds;
            if (_opening)
            {
                if (t < 150)
                {
                    double k = t / 150.0;
                    int size = (int)Math.Round(8 + 54 * Ease(k));
                    SetAnimatedBounds(_animCenterX, _animCenterY, size, size, k);
                }
                else if (t < 320)
                {
                    double k = (t - 150) / 170.0;
                    int width = (int)Math.Round(62 + (_fullWidth - 62) * Ease(k));
                    SetAnimatedBounds(_animCenterX, _animCenterY, width, FullHeight, 1);
                }
                else
                {
                    _animTimer.Stop();
                    _opening = false;
                    FinishOpen();
                }
            }
            else if (_closing)
            {
                if (t < 170)
                {
                    double k = t / 170.0;
                    int width = (int)Math.Round(_fullWidth - (_fullWidth - 62) * Ease(k));
                    SetAnimatedBounds(_animCenterX, _animCenterY, width, FullHeight, 1);
                }
                else if (t < 320)
                {
                    double k = (t - 170) / 150.0;
                    int size = (int)Math.Round(62 - 54 * Ease(k));
                    SetAnimatedBounds(_animCenterX, _animCenterY, size, size, 1 - k);
                }
                else
                {
                    _animTimer.Stop();
                    _closing = false;
                    base.Hide();
                    Size = new Size(_fullWidth, FullHeight);
                    Opacity = _baseOpacity;
                }
            }
        }

        private void FinishOpen()
        {
            Size = new Size(_fullWidth, FullHeight);
            Left = _animCenterX - Width / 2;
            Top = _animCenterY - Height / 2;
            Region = RoundedRegion(Size, 23);
            Opacity = _baseOpacity;
            HideControls(false);
            ApplyLayout();
            TopMost = true;
        }

        private void SetAnimatedBounds(int centerX, int centerY, int width, int height, double opacity)
        {
            Size = new Size(Math.Max(4, width), Math.Max(4, height));
            Left = centerX - Width / 2;
            Top = centerY - Height / 2;
            Region = RoundedRegion(Size, Math.Min(31, Height / 2));
            try { Opacity = Math.Max(0, Math.Min(1, _baseOpacity * opacity)); } catch { }
        }

        private void HideControls(bool hidden)
        {
            _status.Visible = !hidden;
            _focus.Visible = !hidden;
            _stop.Visible = !hidden;
            _pause.Visible = !hidden;
            _drawer.Visible = !hidden && _mode == "watch";
            _lock.Visible = !hidden;
            _close.Visible = !hidden;
            _gripLeft.Visible = !hidden && !_locked;
            _gripRight.Visible = !hidden && !_locked;
        }

        private Panel MakeGrip()
        {
            Panel grip = new Panel();
            grip.Cursor = Cursors.SizeWE;
            // 用胶囊同色而不是透明：透明 Panel 在圆角处会透出系统底色，看着像两个灰块
            grip.BackColor = BackColor;
            grip.MouseDown += ResizeDown;
            grip.MouseMove += ResizeMove;
            grip.MouseUp += ResizeUp;
            Controls.Add(grip);
            _hit.Add(grip);
            return grip;
        }

        // 观看/控制时，胶囊的空白和文字区域不吞点击：只有这些控件真正接收鼠标。
        // 文字层不吃点击：点穿到下面的游戏/窗口，免得胶囊挡住操作。
        private class PassThroughLabel : Label
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0084)
                {
                    m.Result = new IntPtr(-1); // HTTRANSPARENT
                    return;
                }
                base.WndProc(ref m);
            }
        }

        private readonly List<Control> _hit = new List<Control>();

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084 && !_drawerOpen) // WM_NCHITTEST
            {
                int lp = m.LParam.ToInt32();
                Point screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                Point client = PointToClient(screen);
                bool hit = false;
                foreach (Control control in _hit)
                {
                    if (control == null || !control.Visible) continue;
                    if (control.Bounds.Contains(client)) { hit = true; break; }
                }
                if (!hit)
                {
                    m.Result = new IntPtr(-1); // HTTRANSPARENT：点穿到下面的窗口
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private void ResizeDown(object sender, MouseEventArgs e)
        {
            if (_locked || e.Button != MouseButtons.Left || Injected()) return;
            _resizing = true;
            _resizeFromLeft = ReferenceEquals(sender, _gripLeft);
            _resizeStartX = Cursor.Position.X;
            _resizeStartWidth = Width;
            _resizeCenterX = Left + Width / 2;
            Control control = sender as Control;
            if (control != null) control.Capture = true;
        }

        private void ResizeMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            int dx = Cursor.Position.X - _resizeStartX;
            int delta = _resizeFromLeft ? -dx : dx;
            int newWidth = Math.Max(300, Math.Min(900, _resizeStartWidth + delta * 2));
            if (newWidth == Width) return;
            Width = newWidth;
            Left = _resizeCenterX - Width / 2;
            Region = RoundedRegion(Size, Math.Min(31, Height / 2));
            ApplyLayout();
        }

        private void ResizeUp(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            Control control = sender as Control;
            if (control != null) control.Capture = false;
            _fullWidth = Width;
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["width"] = Width;
            data["x"] = Left;
            data["y"] = Top;
            Overlay.Raise("overlay.resized", data);
        }

        private static double Ease(double k)
        {
            if (k < 0) k = 0;
            if (k > 1) k = 1;
            return 1 - Math.Pow(1 - k, 3);
        }

        public void DodgeIfCovered(int x, int y, int holdMs)
        {
            if (!Visible) return;
            Rectangle r = Bounds;
            if (x < r.Left - 4 || x > r.Right + 4 || y < r.Top - 4 || y > r.Bottom + 4) return;
            Dodge(holdMs);
        }

        private void Dodge(int holdMs)
        {
            _dodged = true;
            _animTimer.Stop();
            _opening = false;
            _closing = false;
            _hideTimer.Stop();
            base.Hide();
            Size = new Size(_fullWidth, FullHeight);
            Opacity = _baseOpacity;
            _dodgeTimer.Stop();
            _dodgeTimer.Interval = Math.Max(400, holdMs);
            _dodgeTimer.Start();
        }

        /** 我们自己注入的鼠标事件点不到悬浮岛按钮，避免控制电脑时误触。 */
        private static bool Injected()
        {
            try { return Native.GetMessageExtraInfo() == new IntPtr(0x1C0DE1); }
            catch { return false; }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                _focusTimer.Start();
                UpdateFocus();
            }
            else
            {
                _focusTimer.Stop();
            }
        }

        /** 第二行：当前聚焦窗口的标题和进程，太长就截断。 */
        private void UpdateFocus()
        {
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                string title = InputLog.WindowTitle(fg);
                string exe = InputLog.ExeOf(fg);
                string line = title;
                if (!string.IsNullOrEmpty(exe)) line = string.IsNullOrEmpty(line) ? exe : line + "  (" + exe + ")";
                if (string.IsNullOrEmpty(line)) line = "（没有聚焦窗口）";
                // 不按固定字数截断，交给 AutoEllipsis 按当前窗口宽度显示省略号
                if (line.Length > 160) line = line.Substring(0, 159) + "…";
                _focus.Text = line;
            }
            catch
            {
                _focus.Text = "";
            }
        }

        private void ToggleLock()
        {
            _locked = !_locked;
            _lock.Locked = _locked;
            _lock.Cursor = _locked ? Cursors.Default : Cursors.Hand;
            ApplyLayout();
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["locked"] = _locked;
            Overlay.Raise("overlay.locked", data);
        }

        private void DragDown(object sender, MouseEventArgs e)
        {
            if (_locked || e.Button != MouseButtons.Left) return;
            if (Injected()) return;
            _dragging = true;
            _moved = false;
            _dragStart = Cursor.Position;
            _windowStart = Location;
        }

        private void DragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _locked) return;
            Point now = Cursor.Position;
            int dx = now.X - _dragStart.X;
            int dy = now.Y - _dragStart.Y;
            if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) _moved = true;
            if (!_moved) return;
            Location = new Point(_windowStart.X + dx, _windowStart.Y + dy);
        }

        private void DragUp(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            // 只是点了一下锁/状态文字，不算拖动，不保存位置
            if (!_moved) return;
            _moved = false;
            _position = "custom";
            _customX = Left;
            _customY = Top;
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["x"] = Left;
            data["y"] = Top;
            Overlay.Raise("overlay.moved", data);
        }

        private static Region RoundedRegion(Size size, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(size.Width - d, 0, d, d, 270, 90);
            path.AddArc(size.Width - d, size.Height - d, d, d, 0, 90);
            path.AddArc(0, size.Height - d, d, d, 90, 90);
            path.CloseFigure();
            return new Region(path);
        }
    }

    /// <summary>锁按钮：直接画图标，关闭时是开锁，锁定后是关锁。</summary>
    internal sealed class LockGlyph : Label
    {
        private bool _locked;

        public LockGlyph()
        {
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Text = "";
        }

        public bool Locked
        {
            get { return _locked; }
            set { _locked = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color color = _locked ? Color.FromArgb(205, 214, 228) : Color.FromArgb(110, 160, 255);
            using (Pen pen = new Pen(color, 1.8f))
            using (SolidBrush brush = new SolidBrush(color))
            {
                int cx = Width / 2;
                int cy = Height / 2;
                Rectangle body = new Rectangle(cx - 6, cy - 1, 12, 9);
                if (_locked)
                {
                    e.Graphics.DrawArc(pen, cx - 5, cy - 9, 10, 12, 180, 180);
                }
                else
                {
                    // 开锁：锁梁抬起来偏左，留个缺口
                    e.Graphics.DrawArc(pen, cx - 8, cy - 10, 10, 12, 180, 120);
                }
                e.Graphics.FillRectangle(brush, body);
                e.Graphics.DrawRectangle(pen, body);
            }
        }
    }

    internal sealed class Server
    {
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly object _outGate = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Dictionary<string, object>> _watchFrames = new List<Dictionary<string, object>>();
        private readonly object _watchSync = new object();
        private volatile bool _watchRun;
        private Thread _watchThread;
        private string _watchDir;
        private double _watchFps = 2;
        private double _watchIdleFps = 0.5;
        private int _watchIdleAfterMs = 2000;
        private DateTime _lastChangeAt = DateTime.Now;
        private double _watchThreshold = 0.004;
        private int _watchMaxWidth = 1600;
        private int _watchSeq;
        private float[] _lastKeptGrid;
        private DateTime _watchStart = DateTime.Now;
        private bool _watcherStartedByLearn;

        public void Run()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }
            InputLog.Install();
            Overlay.Event = EmitEvent;

            StreamReader reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                Dictionary<string, object> req = null;
                try { req = _json.DeserializeObject(line) as Dictionary<string, object>; }
                catch (Exception ex) { Emit(null, false, null, "请求不是合法 JSON：" + ex.Message); continue; }
                if (req == null) { Emit(null, false, null, "空请求"); continue; }

                object id;
                req.TryGetValue("id", out id);
                string cmd = Str(req, "cmd");
                Dictionary<string, object> args = GetDict(req, "args");
                try
                {
                    object result = Dispatch(cmd, args);
                    Emit(id, true, result, null);
                }
                catch (Exception ex)
                {
                    Emit(id, false, null, cmd + "：" + ex.Message);
                }
            }
            StopWatch();
            InputLog.Uninstall();
        }

        private void Emit(object id, bool ok, object result, string error)
        {
            Dictionary<string, object> resp = new Dictionary<string, object>();
            resp["id"] = id;
            resp["ok"] = ok;
            if (ok) resp["result"] = result;
            else resp["error"] = error;
            string text = _json.Serialize(resp);
            lock (_outGate)
            {
                Console.Out.WriteLine(text);
                Console.Out.Flush();
            }
        }

        /** 悬浮窗发来的事件（停止、拖动、锁定），没有 id，上层按 event 字段处理。 */
        private void EmitEvent(string name, Dictionary<string, object> data)
        {
            Dictionary<string, object> ev = new Dictionary<string, object>();
            ev["event"] = name;
            if (data != null)
                foreach (KeyValuePair<string, object> pair in data) ev[pair.Key] = pair.Value;
            string text = _json.Serialize(ev);
            lock (_outGate)
            {
                Console.Out.WriteLine(text);
                Console.Out.Flush();
            }
        }

        private object Dispatch(string cmd, Dictionary<string, object> args)
        {
            if (cmd == null) throw new InvalidOperationException("缺少 cmd");
            switch (cmd)
            {
                case "ping": return new Dictionary<string, object> { { "pong", true }, { "pid", Process.GetCurrentProcess().Id } };
                case "info": return Info();
                case "shot": return Shot(args);
                case "compare": return Compare(args);
                case "move": return Move(args);
                case "click": return Click(args);
                case "drag": return Drag(args);
                case "scroll": return Scroll(args);
                case "key": return Key(args);
                case "type": return TypeText(args);
                case "elements": return Elements(args);
                case "probe": return Probe(args);
                case "text": return WindowText(args);
                case "windows": return Windows();
                case "watch.start": return WatchStart(args);
                case "watch.stop": return WatchStop();
                case "watch.status": return WatchStatus();
                case "watch.peek": return WatchPeek(args);
                case "watch.snapshot": return WatchSnapshot(args);
                case "learn.start": return LearnStart(args);
                case "learn.stop": return LearnStop();
                case "learn.status": return LearnStatus();
                case "overlay.config": return OverlayConfigure(args);
                case "overlay.state": return OverlayState(args);
                case "overlay.preview": return OverlayPreview(args);
                case "overlay.alert": return OverlayAlert(args);
                case "overlay.chatlog": return OverlayChatLog(args);
                case "overlay.drawer": Overlay.ToggleDrawer(); return new Dictionary<string, object> { { "ok", true } };
                case "overlay.status": return OverlayStatus();
                case "shutdown": StopWatch(); return new Dictionary<string, object> { { "bye", true } };
                default: throw new InvalidOperationException("未知命令：" + cmd);
            }
        }

        // ---------- 基础信息 ----------

        private static Rectangle VirtualScreen()
        {
            int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0)
            {
                w = Native.GetSystemMetrics(0);
                h = Native.GetSystemMetrics(1);
                x = 0;
                y = 0;
            }
            return new Rectangle(x, y, w, h);
        }

        private static List<Dictionary<string, object>> Monitors()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["x"] = r.Left; m["y"] = r.Top;
                    m["w"] = r.Right - r.Left; m["h"] = r.Bottom - r.Top;
                    list.Add(m);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        private static Dictionary<string, object> ForegroundInfo()
        {
            IntPtr fg = Native.GetForegroundWindow();
            RECT r;
            Native.GetWindowRect(fg, out r);
            uint dpi = 96;
            try { dpi = Native.GetDpiForWindow(fg); if (dpi == 0) dpi = 96; } catch { }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["hwnd"] = fg.ToInt64();
            d["title"] = InputLog.WindowTitle(fg);
            string exe = InputLog.ExeOf(fg);
            if (exe != null) d["exe"] = exe;
            Dictionary<string, object> rect = new Dictionary<string, object>();
            rect["x"] = r.Left; rect["y"] = r.Top;
            rect["w"] = r.Right - r.Left; rect["h"] = r.Bottom - r.Top;
            d["rect"] = rect;
            d["dpi"] = (int)dpi;
            return d;
        }

        private object Info()
        {
            Rectangle vs = VirtualScreen();
            POINT p;
            Native.GetCursorPos(out p);
            Dictionary<string, object> d = new Dictionary<string, object>();
            Dictionary<string, object> screen = new Dictionary<string, object>();
            screen["x"] = vs.X; screen["y"] = vs.Y; screen["w"] = vs.Width; screen["h"] = vs.Height;
            d["screen"] = screen;
            d["monitors"] = Monitors();
            d["cursor"] = new Dictionary<string, object> { { "x", p.X }, { "y", p.Y } };
            d["foreground"] = ForegroundInfo();
            d["pid"] = Process.GetCurrentProcess().Id;
            LASTINPUTINFO lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            try
            {
                if (Native.GetLastInputInfo(ref lii))
                    d["idleMs"] = (int)((uint)Environment.TickCount - lii.dwTime);
                else d["idleMs"] = 0;
            }
            catch { d["idleMs"] = 0; }
            d["userIdleMs"] = InputLog.UserIdleMs;
            d["watching"] = _watchRun;
            d["learning"] = InputLog.Learning;
            d["uptimeMs"] = _clock.ElapsedMilliseconds;
            try { d["dpiSystem"] = (int)Native.GetDpiForSystem(); } catch { d["dpiSystem"] = 96; }
            return d;
        }

        // ---------- 截图 ----------

        private static Rectangle RegionArg(Dictionary<string, object> a)
        {
            if (a == null) return VirtualScreen();
            Dictionary<string, object> r = GetDict(a, "region");
            if (r != null)
            {
                int rr = Int(r, "w", -1);
                if (rr > 0) return new Rectangle(Int(r, "x", 0), Int(r, "y", 0), rr, Int(r, "h", 0));
            }
            if (a.ContainsKey("region") && a["region"] is object[])
            {
                object[] arr = (object[])a["region"];
                if (arr.Length == 4)
                    return new Rectangle(ToInt(arr[0], 0), ToInt(arr[1], 0), ToInt(arr[2], 1), ToInt(arr[3], 1));
            }
            return VirtualScreen();
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

        private static Bitmap Capture(Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) throw new InvalidOperationException("截图区域为空");
            // 用 GDI BitBlt 而不是 GDI+ CopyFromScreen：后者在观看线程里抓全屏会周期性崩掉进程
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new InvalidOperationException("抓屏失败（拿不到屏幕 DC）");
            try
            {
                IntPtr mem = CreateCompatibleDC(screen);
                if (mem == IntPtr.Zero) throw new InvalidOperationException("抓屏失败（DC 创建失败）");
                try
                {
                    IntPtr hbmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
                    if (hbmp == IntPtr.Zero) throw new InvalidOperationException("抓屏失败（位图创建失败）");
                    try
                    {
                        IntPtr old = SelectObject(mem, hbmp);
                        try
                        {
                            if (!BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020))
                                throw new InvalidOperationException("抓屏失败（BitBlt 返回 false）");
                        }
                        finally { SelectObject(mem, old); }
                        using (Bitmap raw = Image.FromHbitmap(hbmp))
                            return new Bitmap(raw);
                    }
                    finally { DeleteObject(hbmp); }
                }
                finally { DeleteDC(mem); }
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        private object Shot(Dictionary<string, object> a)
        {
            if (a == null) a = new Dictionary<string, object>();
            string path = Str(a, "path");
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("shot 需要 path");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            Rectangle region = RegionArg(a);
            int maxWidth = Int(a, "maxWidth", 0);
            Dictionary<string, object> mark = GetDict(a, "mark");
            int px = 0, py = 0;
            bool hasPoint = false;
            if (mark != null && mark.ContainsKey("x"))
            {
                px = Int(mark, "x", 0);
                py = Int(mark, "y", 0);
                hasPoint = true;
            }
            bool grid = Bool(a, "grid", false);
            string label = Str(a, "label");

            using (Bitmap bmp = Capture(region))
            {
                int origW = bmp.Width;
                if (grid || hasPoint)
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        Annotate(g, bmp, region, px, py, hasPoint, grid, label);
                    }
                }

                Bitmap output = bmp;
                bool ownsOutput = false;
                double applied = 1.0;
                if (maxWidth > 0 && bmp.Width > maxWidth)
                {
                    double scale = (double)maxWidth / bmp.Width;
                    int nh = Math.Max(1, (int)Math.Round(bmp.Height * scale));
                    Bitmap small = new Bitmap(maxWidth, nh, PixelFormat.Format32bppArgb);
                    using (Graphics g2 = Graphics.FromImage(small))
                    {
                        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g2.DrawImage(bmp, 0, 0, maxWidth, nh);
                    }
                    output = small;
                    ownsOutput = true;
                    applied = scale;
                }

                // up：局部放大（1–4 倍），NearestNeighbor 保留像素边界，方便数像素/读小字
                int up = Math.Max(1, Math.Min(4, Int(a, "up", 1)));
                if (up > 1)
                {
                    int w2 = output.Width * up;
                    int h2 = output.Height * up;
                    Bitmap big = new Bitmap(w2, h2, PixelFormat.Format32bppArgb);
                    using (Graphics g3 = Graphics.FromImage(big))
                    {
                        g3.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g3.PixelOffsetMode = PixelOffsetMode.Half;
                        g3.DrawImage(output, 0, 0, w2, h2);
                    }
                    if (ownsOutput) output.Dispose();
                    output = big;
                    ownsOutput = true;
                    applied = applied * up;
                }

                int outW = output.Width;
                int outH = output.Height;
                SaveJpeg(output, path, 82);
                if (ownsOutput) output.Dispose();

                Dictionary<string, object> res = new Dictionary<string, object>();
                res["path"] = path;
                res["width"] = outW;
                res["height"] = outH;
                res["originX"] = region.X;
                res["originY"] = region.Y;
                res["scale"] = applied;
                res["up"] = up;
                return res;
            }
        }

        private static void Annotate(Graphics g, Bitmap bmp, Rectangle origin, int px, int py, bool hasPoint, bool grid, string label)
        {
            try { g.SmoothingMode = SmoothingMode.AntiAlias; } catch { }

            if (grid)
            {
                using (Pen p = new Pen(Color.FromArgb(70, 255, 0, 255), 1))
                {
                    for (int x = 0; x < bmp.Width; x += 100) g.DrawLine(p, x, 0, x, bmp.Height);
                    for (int y = 0; y < bmp.Height; y += 100) g.DrawLine(p, 0, y, bmp.Width, y);
                }
                try
                {
                    using (Font f = new Font("Segoe UI", 18f, FontStyle.Regular))
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(150, 255, 0, 255)))
                    {
                        for (int x = 0; x < bmp.Width; x += 200)
                            for (int y = 0; y < bmp.Height; y += 200)
                                g.DrawString(string.Format("{0},{1}", origin.X + x, origin.Y + y), f, br, x + 2, y + 1);
                    }
                }
                catch { }
            }

            if (!hasPoint) return;
            int lx = px - origin.X;
            int ly = py - origin.Y;
            using (Pen pen = new Pen(Color.FromArgb(255, 220, 30, 60), 2))
            {
                g.DrawEllipse(pen, lx - 14, ly - 14, 28, 28);
                g.DrawLine(pen, lx - 34, ly, lx - 6, ly);
                g.DrawLine(pen, lx + 6, ly, lx + 34, ly);
                g.DrawLine(pen, lx, ly - 34, lx, ly - 6);
                g.DrawLine(pen, lx, ly + 6, lx, ly + 34);
            }

            string text = label != null ? label : string.Format("({0}, {1})", px, py);
            try
            {
                using (Font font = new Font("Segoe UI", 10f, FontStyle.Bold))
                {
                    SizeF size = g.MeasureString(text, font);
                    float bx = Clamp(lx + 22, 4, bmp.Width - size.Width - 8);
                    float by = Clamp(ly + 18, 4, bmp.Height - size.Height - 8);
                    using (SolidBrush back = new SolidBrush(Color.FromArgb(200, 15, 15, 15)))
                        g.FillRectangle(back, bx - 4, by - 2, size.Width + 8, size.Height + 4);
                    g.DrawString(text, font, Brushes.White, bx, by);
                }
            }
            catch { }

            // 放大镜：目标点周围 180x120，2 倍贴到右下角
            int cw = 180, ch = 120;
            if (bmp.Width > cw * 3 && bmp.Height > ch * 3)
            {
                Rectangle src = new Rectangle(lx - cw / 2, ly - ch / 2, cw, ch);
                src = ClampRect(src, new Rectangle(0, 0, bmp.Width, bmp.Height));
                if (src.Width > 0 && src.Height > 0)
                {
                    Rectangle dst = new Rectangle(bmp.Width - cw * 2 - 14, bmp.Height - ch * 2 - 14, cw * 2, ch * 2);
                    try
                    {
                        using (Bitmap crop = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb))
                        {
                            using (Graphics cg = Graphics.FromImage(crop))
                                cg.DrawImage(bmp, new Rectangle(0, 0, src.Width, src.Height), src, GraphicsUnit.Pixel);
                            g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            g.DrawImage(crop, dst);
                        }
                        g.DrawRectangle(Pens.DeepSkyBlue, dst);
                    }
                    catch { }
                }
            }
        }

        private static float Clamp(float v, float min, float max)
        {
            if (max < min) return min;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static Rectangle ClampRect(Rectangle r, Rectangle bounds)
        {
            int x = Math.Max(bounds.X, Math.Min(r.X, bounds.Right));
            int y = Math.Max(bounds.Y, Math.Min(r.Y, bounds.Bottom));
            int right = Math.Max(x, Math.Min(r.Right, bounds.Right));
            int bottom = Math.Max(y, Math.Min(r.Bottom, bounds.Bottom));
            return new Rectangle(x, y, right - x, bottom - y);
        }

        private static ImageCodecInfo JpegEncoder()
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            foreach (ImageCodecInfo c in codecs)
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        private static void SaveJpeg(Bitmap bmp, string path, long quality)
        {
            ImageCodecInfo codec = JpegEncoder();
            if (codec == null) { bmp.Save(path, ImageFormat.Jpeg); return; }
            using (EncoderParameters ps = new EncoderParameters(1))
            {
                ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bmp.Save(path, codec, ps);
            }
        }

        // ---------- 差分 ----------

        private static float[] Grid(Bitmap bmp)
        {
            return GridCore(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        }

        /** 只对 bmp 里的一块区域做 64×36 采样，用于点周围的高灵敏差分。 */
        private static float[] GridCore(Bitmap bmp, Rectangle area)
        {
            const int GW = 64, GH = 36;
            float[] grid = new float[GW * GH * 3];
            if (area.Width < 1) area.Width = 1;
            if (area.Height < 1) area.Height = 1;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                IntPtr scan = data.Scan0;
                for (int cy = 0; cy < GH; cy++)
                {
                    int y0 = area.Y + cy * area.Height / GH;
                    int y1 = area.Y + Math.Max(1, (cy + 1) * area.Height / GH);
                    if (y1 > area.Bottom) y1 = area.Bottom;
                    if (y1 <= y0) y1 = y0 + 1;
                    int yA = y0 + (y1 - y0) / 4;
                    int yB = y0 + (y1 - y0) * 3 / 4;
                    for (int cx = 0; cx < GW; cx++)
                    {
                        int x0 = area.X + cx * area.Width / GW;
                        int x1 = area.X + Math.Max(1, (cx + 1) * area.Width / GW);
                        if (x1 > area.Right) x1 = area.Right;
                        if (x1 <= x0) x1 = x0 + 1;
                        int xA = x0 + (x1 - x0) / 4;
                        int xB = x0 + (x1 - x0) * 3 / 4;
                        int r = 0, gg = 0, b = 0;
                        r += ReadPixel(scan, stride, xA, yA, 2);
                        gg += ReadPixel(scan, stride, xA, yA, 1);
                        b += ReadPixel(scan, stride, xA, yA, 0);
                        r += ReadPixel(scan, stride, xB, yA, 2);
                        gg += ReadPixel(scan, stride, xB, yA, 1);
                        b += ReadPixel(scan, stride, xB, yA, 0);
                        r += ReadPixel(scan, stride, xA, yB, 2);
                        gg += ReadPixel(scan, stride, xA, yB, 1);
                        b += ReadPixel(scan, stride, xA, yB, 0);
                        r += ReadPixel(scan, stride, xB, yB, 2);
                        gg += ReadPixel(scan, stride, xB, yB, 1);
                        b += ReadPixel(scan, stride, xB, yB, 0);
                        int i = (cy * GW + cx) * 3;
                        grid[i] = r / 4f;
                        grid[i + 1] = gg / 4f;
                        grid[i + 2] = b / 4f;
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return grid;
        }

        private static int ReadPixel(IntPtr scan, int stride, int x, int y, int channel)
        {
            int offset = y * stride + x * 4 + channel;
            return Marshal.ReadByte(scan, offset);
        }

        private static Dictionary<string, object> DiffGrid(float[] a, float[] b, int gw, int gh, Rectangle origin, double cellThreshold)
        {
            int changed = 0;
            int minx = gw, miny = gh, maxx = -1, maxy = -1;
            double worst = 0;
            for (int cy = 0; cy < gh; cy++)
            {
                for (int cx = 0; cx < gw; cx++)
                {
                    int i = (cy * gw + cx) * 3;
                    double d = (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2])) / 3.0;
                    if (d > cellThreshold)
                    {
                        changed++;
                        if (cx < minx) minx = cx;
                        if (cx > maxx) maxx = cx;
                        if (cy < miny) miny = cy;
                        if (cy > maxy) maxy = cy;
                    }
                    if (d > worst) worst = d;
                }
            }
            double cellW = (double)origin.Width / gw;
            double cellH = (double)origin.Height / gh;
            Dictionary<string, object> bbox = new Dictionary<string, object>();
            bbox["x"] = 0; bbox["y"] = 0; bbox["w"] = 0; bbox["h"] = 0;
            if (maxx >= 0)
            {
                bbox["x"] = origin.X + (int)Math.Floor(minx * cellW);
                bbox["y"] = origin.Y + (int)Math.Floor(miny * cellH);
                bbox["w"] = (int)Math.Ceiling((maxx - minx + 1) * cellW);
                bbox["h"] = (int)Math.Ceiling((maxy - miny + 1) * cellH);
            }
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["ratio"] = Math.Round((double)changed / (gw * gh), 5);
            res["changedCells"] = changed;
            res["cells"] = gw * gh;
            res["bbox"] = bbox;
            res["peak"] = Math.Round(worst, 1);
            return res;
        }

        private object Compare(Dictionary<string, object> a)
        {
            string pa = Str(a, "pathA");
            string pb = Str(a, "pathB");
            if (string.IsNullOrEmpty(pa) || string.IsNullOrEmpty(pb)) throw new InvalidOperationException("compare 需要 pathA / pathB");
            Rectangle region = RegionArg(a);
            using (Bitmap A = new Bitmap(pa))
            using (Bitmap B = new Bitmap(pb))
            {
                if (A.Width != B.Width || A.Height != B.Height)
                {
                    Dictionary<string, object> mismatch = new Dictionary<string, object>();
                    mismatch["ratio"] = 1.0;
                    mismatch["changedCells"] = 2304;
                    mismatch["cells"] = 2304;
                    mismatch["sizeMismatch"] = true;
                    Dictionary<string, object> bb = new Dictionary<string, object>();
                    bb["x"] = region.X; bb["y"] = region.Y; bb["w"] = region.Width; bb["h"] = region.Height;
                    mismatch["bbox"] = bb;
                    return mismatch;
                }
                float[] ga = Grid(A);
                float[] gb = Grid(B);
                Dictionary<string, object> res = DiffGrid(ga, gb, 64, 36, region, 10.0);
                res["imageWidth"] = A.Width;
                res["imageHeight"] = A.Height;
                res["scale"] = (double)A.Width / Math.Max(1, region.Width);

                // 目标周边的小区域单独比一遍：小按钮/勾选框的变化在全屏里太细，这里才能抓住。
                List<Rectangle> regions = RegionsArg(a);
                if (regions.Count > 0)
                {
                    List<Dictionary<string, object>> tight = new List<Dictionary<string, object>>();
                    double sx = (double)A.Width / Math.Max(1, region.Width);
                    double sy = (double)A.Height / Math.Max(1, region.Height);
                    Rectangle imageRect = new Rectangle(0, 0, A.Width, A.Height);
                    foreach (Rectangle r in regions)
                    {
                        Rectangle crop = new Rectangle(
                            (int)Math.Round((r.X - region.X) * sx),
                            (int)Math.Round((r.Y - region.Y) * sy),
                            Math.Max(1, (int)Math.Round(r.Width * sx)),
                            Math.Max(1, (int)Math.Round(r.Height * sy)));
                        crop = Rectangle.Intersect(crop, imageRect);
                        if (crop.Width < 2 || crop.Height < 2)
                        {
                            Dictionary<string, object> empty = new Dictionary<string, object>();
                            empty["ratio"] = 0.0;
                            empty["changedCells"] = 0;
                            empty["cells"] = 64 * 36;
                            tight.Add(empty);
                            continue;
                        }
                        float[] ra = GridCore(A, crop);
                        float[] rb = GridCore(B, crop);
                        tight.Add(DiffGrid(ra, rb, 64, 36, r, 10.0));
                    }
                    res["regions"] = tight;
                }
                return res;
            }
        }

        // ---------- 鼠标键盘 ----------

        private static readonly IntPtr Marker = new IntPtr(0x1C0DE1);

        private static INPUT MouseInput(uint flags, int dx, int dy, int data)
        {
            INPUT inp = new INPUT();
            inp.type = 0;
            inp.u.mi.dx = dx;
            inp.u.mi.dy = dy;
            inp.u.mi.mouseData = unchecked((uint)data);
            inp.u.mi.dwFlags = flags;
            inp.u.mi.dwExtraInfo = Marker;
            return inp;
        }

        private static INPUT KeyInput(ushort vk, ushort scan, uint flags)
        {
            INPUT inp = new INPUT();
            inp.type = 1;
            inp.u.ki.wVk = vk;
            inp.u.ki.wScan = scan;
            inp.u.ki.dwFlags = flags;
            inp.u.ki.dwExtraInfo = Marker;
            return inp;
        }

        private static void Send(INPUT input)
        {
            SendAll(new INPUT[] { input });
        }

        private static void SendAll(INPUT[] inputs)
        {
            uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Length)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("SendInput 被系统拒绝（目标可能是管理员权限或安全桌面），Win32 错误 " + err);
            }
        }

        private static void MoveTo(int x, int y)
        {
            Overlay.DodgeIfCovered(x, y, 700);
            if (!Native.SetCursorPos(x, y)) throw new InvalidOperationException("SetCursorPos 失败");
            Send(MouseInput(Native.MOUSEEVENTF_MOVE, 0, 0, 0));
            InputLog.NoteCursorMove(x, y);
        }

        /** 用户优先：真人刚动过鼠标键盘时就别抢。 */
        private static void EnsureUserIdle(Dictionary<string, object> a)
        {
            if (!Bool(a, "yieldToUser", true)) return;
            int idle = InputLog.UserIdleMs;
            if (idle < 350)
                throw new InvalidOperationException("USER_BUSY: 你正在操作鼠标键盘（" + idle + "ms 前还有输入），我先让开。");
        }

        private static object Move(Dictionary<string, object> a)
        {
            EnsureUserIdle(a);
            int x = Int(a, "x", 0);
            int y = Int(a, "y", 0);
            MoveTo(x, y);
            return new Dictionary<string, object> { { "x", x }, { "y", y } };
        }

        private static readonly Dictionary<string, ushort> VkMap = BuildVkMap();

        private static Dictionary<string, ushort> BuildVkMap()
        {
            Dictionary<string, ushort> map = new Dictionary<string, ushort>();
            map["ctrl"] = 0x11; map["control"] = 0x11;
            map["shift"] = 0x10;
            map["alt"] = 0x12;
            map["win"] = 0x5B; map["lwin"] = 0x5B; map["rwin"] = 0x5C;
            map["esc"] = 0x1B; map["escape"] = 0x1B;
            map["enter"] = 0x0D; map["return"] = 0x0D;
            map["tab"] = 0x09;
            map["space"] = 0x20;
            map["backspace"] = 0x08; map["back"] = 0x08;
            map["delete"] = 0x2E; map["del"] = 0x2E;
            map["insert"] = 0x2D;
            map["home"] = 0x24; map["end"] = 0x23;
            map["pageup"] = 0x21; map["pagedown"] = 0x22; map["pgup"] = 0x21; map["pgdn"] = 0x22;
            map["up"] = 0x26; map["down"] = 0x28; map["left"] = 0x25; map["right"] = 0x27;
            map["printscreen"] = 0x2C; map["prtsc"] = 0x2C;
            map["capslock"] = 0x14;
            map["`"] = 0xC0; map["-"] = 0xBD; map["="] = 0xBB;
            map["["] = 0xDB; map["]"] = 0xDD; map["\\"] = 0xDC;
            map[";"] = 0xBA; map["'"] = 0xDE; map[","] = 0xBC; map["."] = 0xBE; map["/"] = 0xBF;
            for (int i = 1; i <= 24; i++) map["f" + i] = (ushort)(0x6F + i);
            return map;
        }

        private static ushort ParseMainKey(string part)
        {
            string key = part.Trim().ToLowerInvariant();
            if (key.Length == 0) throw new InvalidOperationException("空的按键");
            ushort vk;
            if (VkMap.TryGetValue(key, out vk)) return vk;
            if (key.Length == 1)
            {
                char c = key[0];
                if (c >= 'a' && c <= 'z') return (ushort)(0x41 + (c - 'a'));
                if (c >= '0' && c <= '9') return (ushort)(0x30 + (c - '0'));
                short scan = Native.VkKeyScan(c);
                if (scan != -1) return (ushort)(scan & 0xFF);
            }
            throw new InvalidOperationException("不认识的按键：" + part);
        }

        private static void PressModifiers(string[] mods, bool down)
        {
            if (mods == null || mods.Length == 0) return;
            for (int i = 0; i < mods.Length; i++)
            {
                ushort vk;
                if (!VkMap.TryGetValue(mods[i].Trim().ToLowerInvariant(), out vk)) continue;
                Send(KeyInput(vk, 0, down ? 0u : Native.KEYEVENTF_KEYUP));
                Thread.Sleep(8);
            }
        }

        private static object Click(Dictionary<string, object> a)
        {
            EnsureUserIdle(a);
            int x = Int(a, "x", 0);
            int y = Int(a, "y", 0);
            int count = Math.Max(1, Math.Min(3, Int(a, "count", 1)));
            string button = (Str(a, "button") ?? "left").ToLowerInvariant();
            string[] mods = StrArray(a, "modifiers");

            uint down, up;
            if (button == "right") { down = Native.MOUSEEVENTF_RIGHTDOWN; up = Native.MOUSEEVENTF_RIGHTUP; }
            else if (button == "middle") { down = Native.MOUSEEVENTF_MIDDLEDOWN; up = Native.MOUSEEVENTF_MIDDLEUP; }
            else { down = Native.MOUSEEVENTF_LEFTDOWN; up = Native.MOUSEEVENTF_LEFTUP; }

            MoveTo(x, y);
            Thread.Sleep(30);
            PressModifiers(mods, true);
            for (int i = 0; i < count; i++)
            {
                Send(MouseInput(down, 0, 0, 0));
                Thread.Sleep(12);
                Send(MouseInput(up, 0, 0, 0));
                if (count > 1) Thread.Sleep(70);
            }
            PressModifiers(mods, false);

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["x"] = x; res["y"] = y; res["button"] = button; res["count"] = count;
            return res;
        }

        private object Drag(Dictionary<string, object> a)
        {
            EnsureUserIdle(a);
            int x = Int(a, "x", 0);
            int y = Int(a, "y", 0);
            int x2 = Int(a, "x2", x);
            int y2 = Int(a, "y2", y);
            int duration = Math.Max(120, Math.Min(3000, Int(a, "duration", 350)));
            string button = (Str(a, "button") ?? "left").ToLowerInvariant();
            Overlay.DodgeIfCovered(x2, y2, duration + 900);
            uint down, up;
            if (button == "right") { down = Native.MOUSEEVENTF_RIGHTDOWN; up = Native.MOUSEEVENTF_RIGHTUP; }
            else { down = Native.MOUSEEVENTF_LEFTDOWN; up = Native.MOUSEEVENTF_LEFTUP; }

            MoveTo(x, y);
            Thread.Sleep(60);
            Send(MouseInput(down, 0, 0, 0));
            int steps = Math.Max(8, duration / 16);
            bool released = false;
            int lastX = x, lastY = y;
            try
            {
                for (int i = 1; i <= steps; i++)
                {
                    // 用户接管：光标被真人挪走了（我们每步都精确落点，偏离就说明有人抢）
                    if (Bool(a, "yieldToUser", true))
                    {
                        POINT cur;
                        if (Native.GetCursorPos(out cur)
                            && (Math.Abs(cur.X - lastX) > 4 || Math.Abs(cur.Y - lastY) > 4))
                        {
                            Send(MouseInput(up, 0, 0, 0));
                            released = true;
                            throw new InvalidOperationException("USER_BUSY: 拖动时你接管了鼠标（在 (" + cur.X + ", " + cur.Y + ")，第 " + i + "/" + steps + " 步），我已经松开。");
                        }
                    }
                    int nx = x + (x2 - x) * i / steps;
                    int ny = y + (y2 - y) * i / steps;
                    Native.SetCursorPos(nx, ny);
                    InputLog.NoteCursorMove(nx, ny);
                    lastX = nx;
                    lastY = ny;
                    Thread.Sleep(duration / steps);
                }
            }
            finally
            {
                if (!released) Send(MouseInput(up, 0, 0, 0));
            }
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["from"] = new Dictionary<string, object> { { "x", x }, { "y", y } };
            res["to"] = new Dictionary<string, object> { { "x", x2 }, { "y", y2 } };
            res["duration"] = duration;
            return res;
        }

        private object Scroll(Dictionary<string, object> a)
        {
            EnsureUserIdle(a);
            int x = Int(a, "x", 0);
            int y = Int(a, "y", 0);
            int delta = Int(a, "delta", -120);
            int times = Math.Max(1, Math.Min(40, Math.Abs(Int(a, "times", 1))));
            MoveTo(x, y);
            Thread.Sleep(20);
            for (int i = 0; i < times; i++)
            {
                Send(MouseInput(Native.MOUSEEVENTF_WHEEL, 0, 0, delta));
                Thread.Sleep(20);
            }
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["x"] = x; res["y"] = y; res["delta"] = delta; res["times"] = times;
            return res;
        }

        private object Key(Dictionary<string, object> a)
        {
            EnsureUserIdle(a);
            string keys = Str(a, "keys");
            if (string.IsNullOrEmpty(keys)) throw new InvalidOperationException("key 需要 keys，例如 ctrl+s");
            string[] parts = keys.Split('+');
            List<string> mods = new List<string>();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string m = parts[i].Trim().ToLowerInvariant();
                if (m.Length > 0) mods.Add(m);
            }
            ushort vk = ParseMainKey(parts[parts.Length - 1]);
            PressModifiers(mods.ToArray(), true);
            Send(KeyInput(vk, 0, 0));
            Thread.Sleep(20);
            Send(KeyInput(vk, 0, Native.KEYEVENTF_KEYUP));
            PressModifiers(mods.ToArray(), false);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["keys"] = keys;
            res["vk"] = (int)vk;
            return res;
        }

        private object TypeText(Dictionary<string, object> a)
        {
            EnsureUserIdle(a);
            string text = Str(a, "text");
            if (text == null) throw new InvalidOperationException("type 需要 text");
            foreach (char ch in text)
            {
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    Send(KeyInput(0x0D, 0, 0));
                    Thread.Sleep(8);
                    Send(KeyInput(0x0D, 0, Native.KEYEVENTF_KEYUP));
                }
                else if (ch == '\t')
                {
                    Send(KeyInput(0x09, 0, 0));
                    Thread.Sleep(8);
                    Send(KeyInput(0x09, 0, Native.KEYEVENTF_KEYUP));
                }
                else
                {
                    Send(KeyInput(0, ch, Native.KEYEVENTF_UNICODE));
                    Send(KeyInput(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
                }
                Thread.Sleep(4);
            }
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["chars"] = text.Length;
            return res;
        }

        // ---------- UI Automation ----------

        /** 查询某个屏幕坐标下面是什么窗口/什么元素，用来说明「点歪了没有」。 */
        private object Probe(Dictionary<string, object> a)
        {
            int x = Int(a, "x", 0);
            int y = Int(a, "y", 0);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["x"] = x;
            res["y"] = y;

            POINT pt = new POINT();
            pt.X = x;
            pt.Y = y;
            IntPtr hwnd = Native.WindowFromPoint(pt);
            IntPtr root = hwnd == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hwnd, 2);
            if (root != IntPtr.Zero)
            {
                res["hwnd"] = root.ToInt64();
                res["title"] = InputLog.WindowTitle(root);
                res["exe"] = InputLog.ExeOf(root);
                StringBuilder cls = new StringBuilder(256);
                if (Native.GetClassName(root, cls, cls.Capacity) > 0) res["class"] = cls.ToString();
            }
            else
            {
                res["title"] = "";
                res["exe"] = "";
            }

            Dictionary<string, object> element = null;
            string error = null;
            Thread worker = new Thread(delegate()
            {
                try
                {
                    AutomationElement el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                    if (el == null) return;
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["name"] = el.Current.Name;
                    string type = "";
                    try { type = el.Current.ControlType == null ? "" : el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); }
                    catch { }
                    e["type"] = type;
                    e["id"] = el.Current.AutomationId;
                    e["class"] = el.Current.ClassName;
                    e["enabled"] = el.Current.IsEnabled;
                    System.Windows.Rect r = el.Current.BoundingRectangle;
                    e["x"] = (int)Math.Round(r.X);
                    e["y"] = (int)Math.Round(r.Y);
                    e["w"] = (int)Math.Round(r.Width);
                    e["h"] = (int)Math.Round(r.Height);
                    element = e;
                }
                catch (Exception ex) { error = ex.Message; }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            worker.Join(2500);
            if (element != null) res["element"] = element;
            if (error != null) res["elementError"] = error;
            return res;
        }

        private object Elements(Dictionary<string, object> a)
        {
            IntPtr hwnd = new IntPtr(Int(a, "hwnd", 0));
            int max = Math.Max(1, Math.Min(400, Int(a, "max", 150)));
            int maxDepth = Math.Max(1, Math.Min(12, Int(a, "depth", 8)));
            List<Dictionary<string, object>> list = null;
            string error = null;
            Thread worker = new Thread(delegate()
            {
                try { list = CollectElements(hwnd, max, maxDepth); }
                catch (Exception ex) { error = ex.Message; }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            if (!worker.Join(5000))
                throw new InvalidOperationException("UI Automation 查询超时（目标窗口可能忙）");
            if (error != null) throw new InvalidOperationException(error);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["hwnd"] = hwnd.ToInt64();
            res["title"] = InputLog.WindowTitle(hwnd == IntPtr.Zero ? Native.GetForegroundWindow() : hwnd);
            res["elements"] = list ?? new List<Dictionary<string, object>>();
            return res;
        }

        private static List<Dictionary<string, object>> CollectElements(IntPtr hwnd, int max, int maxDepth)
        {
            IntPtr target = hwnd == IntPtr.Zero ? Native.GetForegroundWindow() : hwnd;
            if (target == IntPtr.Zero) return new List<Dictionary<string, object>>();
            AutomationElement root;
            try { root = AutomationElement.FromHandle(target); }
            catch { root = AutomationElement.RootElement; }

            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            Queue<KeyValuePair<AutomationElement, int>> queue = new Queue<KeyValuePair<AutomationElement, int>>();
            queue.Enqueue(new KeyValuePair<AutomationElement, int>(root, 0));
            TreeWalker walker = TreeWalker.ControlViewWalker;

            while (queue.Count > 0 && result.Count < max)
            {
                KeyValuePair<AutomationElement, int> item = queue.Dequeue();
                int depth = item.Value;
                if (depth > 0) AddElement(result, item.Key, result.Count);
                if (depth >= maxDepth) continue;

                AutomationElement child = null;
                try { child = walker.GetFirstChild(item.Key); }
                catch { child = null; }
                int guard = 0;
                while (child != null && guard++ < 300)
                {
                    queue.Enqueue(new KeyValuePair<AutomationElement, int>(child, depth + 1));
                    try { child = walker.GetNextSibling(child); }
                    catch { child = null; }
                }
            }
            return result;
        }

        private static void AddElement(List<Dictionary<string, object>> list, AutomationElement el, int index)
        {
            try
            {
                System.Windows.Rect rect = el.Current.BoundingRectangle;
                if (rect.IsEmpty) return;
                string name = el.Current.Name ?? "";
                name = name.Replace("\r", " ").Replace("\n", " ").Trim();
                string type = "";
                try { type = el.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""); }
                catch { }
                string value = "";
                try
                {
                    object pattern;
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                        value = ((ValuePattern)pattern).Current.Value ?? "";
                }
                catch { }
                if (name.Length == 0 && value.Length == 0) return;
                if (name.Length == 0) name = value;

                bool canInvoke = false, canToggle = false, canExpand = false;
                try { object p; canInvoke = el.TryGetCurrentPattern(InvokePattern.Pattern, out p); } catch { }
                try { object p; canToggle = el.TryGetCurrentPattern(TogglePattern.Pattern, out p); } catch { }
                try { object p; canExpand = el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p); } catch { }

                Dictionary<string, object> item = new Dictionary<string, object>();
                item["ref"] = index + 1;
                item["name"] = name.Length > 120 ? name.Substring(0, 120) : name;
                item["type"] = type;
                item["x"] = (int)Math.Round(rect.X);
                item["y"] = (int)Math.Round(rect.Y);
                item["w"] = (int)Math.Round(rect.Width);
                item["h"] = (int)Math.Round(rect.Height);
                item["cx"] = (int)Math.Round(rect.X + rect.Width / 2);
                item["cy"] = (int)Math.Round(rect.Y + rect.Height / 2);
                try { item["enabled"] = el.Current.IsEnabled; } catch { item["enabled"] = true; }
                try { item["offscreen"] = el.Current.IsOffscreen; } catch { item["offscreen"] = false; }
                try { item["focusable"] = el.Current.IsKeyboardFocusable; } catch { item["focusable"] = false; }
                if (value.Length > 0) item["value"] = value.Length > 200 ? value.Substring(0, 200) : value;
                if (canInvoke) item["invoke"] = true;
                if (canToggle) item["toggle"] = true;
                if (canExpand) item["expand"] = true;
                list.Add(item);
            }
            catch { }
        }

        private object WindowText(Dictionary<string, object> a)
        {
            IntPtr hwnd = new IntPtr(Int(a, "hwnd", 0));
            IntPtr target = hwnd == IntPtr.Zero ? Native.GetForegroundWindow() : hwnd;
            int tail = Math.Max(200, Math.Min(20000, Int(a, "tail", 4000)));
            List<Dictionary<string, object>> elements = null;
            Thread worker = new Thread(delegate()
            {
                try { elements = CollectElements(target, 400, 10); }
                catch { elements = new List<Dictionary<string, object>>(); }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            worker.Join(5000);

            StringBuilder sb = new StringBuilder();
            if (elements != null)
            {
                foreach (Dictionary<string, object> e in elements)
                {
                    object offObj;
                    if (e.TryGetValue("offscreen", out offObj) && offObj is bool && (bool)offObj) continue;
                    string name = Convert.ToString(e["name"]);
                    string value = e.ContainsKey("value") ? Convert.ToString(e["value"]) : "";
                    if (name.Length > 0) sb.AppendLine(name);
                    if (value.Length > 0 && value != name) sb.AppendLine(value);
                    if (sb.Length > 200000) break;
                }
            }
            string text = sb.ToString();
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["title"] = InputLog.WindowTitle(target);
            string exe = InputLog.ExeOf(target);
            if (exe != null) res["exe"] = exe;
            res["length"] = text.Length;
            res["truncated"] = text.Length > tail;
            res["text"] = text.Length > tail ? text.Substring(text.Length - tail) : text;
            return res;
        }

        private object Windows()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        if (!Native.IsWindowVisible(hWnd)) return true;
                        if (Native.GetWindowTextLength(hWnd) <= 0) return true;
                        RECT r;
                        if (!Native.GetWindowRect(hWnd, out r)) return true;
                        if (r.Right - r.Left < 40 || r.Bottom - r.Top < 30) return true;
                        Dictionary<string, object> w = new Dictionary<string, object>();
                        w["hwnd"] = hWnd.ToInt64();
                        w["title"] = InputLog.WindowTitle(hWnd);
                        string exe = InputLog.ExeOf(hWnd);
                        if (exe != null) w["exe"] = exe;
                        w["x"] = r.Left; w["y"] = r.Top;
                        w["w"] = r.Right - r.Left; w["h"] = r.Bottom - r.Top;
                        list.Add(w);
                    }
                    catch { }
                    return list.Count < 30;
                }, IntPtr.Zero);
            }
            catch { }
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["windows"] = list;
            return res;
        }

        // ---------- 观看 ----------

        private object WatchStart(Dictionary<string, object> a)
        {
            if (_watchRun) return WatchStatus();
            string dir = Str(a, "dir");
            if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("watch.start 需要 dir");
            Directory.CreateDirectory(Path.Combine(dir, "frames"));
            // 每次开始观看都清掉上一轮的变化帧，避免旧图越积越多
            try
            {
                foreach (string old in Directory.GetFiles(Path.Combine(dir, "frames"), "*.jpg")) File.Delete(old);
                string oldLatest = Path.Combine(dir, "latest.jpg");
                if (File.Exists(oldLatest)) File.Delete(oldLatest);
            }
            catch { }
            _watchDir = dir;
            _watchFps = Math.Max(0.5, Math.Min(10, Num(a, "fps", 2)));
            _watchIdleFps = Math.Max(0.2, Math.Min(_watchFps, Num(a, "idleFps", 0.5)));
            _watchIdleAfterMs = Math.Max(300, Math.Min(60000, Int(a, "idleAfterMs", 2000)));
            _lastChangeAt = DateTime.Now;
            _watchThreshold = Math.Max(0.0005, Math.Min(0.2, Num(a, "threshold", 0.004)));
            _watchMaxWidth = Math.Max(640, Math.Min(2560, Int(a, "maxWidth", 1600)));
            _watchSeq = 0;
            _lastKeptGrid = null;
            lock (_watchSync) { _watchFrames.Clear(); }
            _watchStart = DateTime.Now;
            _watchRun = true;
            _watchThread = new Thread(WatchLoop);
            _watchThread.IsBackground = true;
            _watchThread.SetApartmentState(ApartmentState.STA);
            _watchThread.Start();
            Thread.Sleep(120);
            return WatchStatus();
        }

        private void WatchLoop()
        {
            while (_watchRun)
            {
                try { WatchTick(); }
                catch (Exception ex)
                {
                    try
                    {
                        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cch-watch.log");
                        System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " tick error " + ex.GetType().Name + ": " + ex.Message + "\n");
                    }
                    catch { }
                }
                // 空闲降频：连续一段时间没有变化就慢下来，一有变化立刻回到设定帧率
                int quietMs = (int)(DateTime.Now - _lastChangeAt).TotalMilliseconds;
                double fps = quietMs >= _watchIdleAfterMs ? Math.Min(_watchFps, _watchIdleFps) : _watchFps;
                int interval = (int)Math.Max(100, 1000.0 / fps);
                for (int slept = 0; slept < interval && _watchRun; slept += 50) Thread.Sleep(50);
            }
        }

        private void WatchTick()
        {
            Rectangle vs = VirtualScreen();
            using (Bitmap bmp = Capture(vs))
            {
                float[] grid = Grid(bmp);
                Dictionary<string, object> diff = null;
                if (_lastKeptGrid != null)
                    diff = DiffGrid(_lastKeptGrid, grid, 64, 36, vs, 16.0);
                bool keep = _lastKeptGrid == null || (diff != null && Convert.ToDouble(diff["ratio"]) >= _watchThreshold);
                if (!keep) return;

                _watchSeq++;
                string path = Path.Combine(_watchDir, "frames", string.Format("f{0:D4}.jpg", _watchSeq));
                Bitmap save = bmp;
                Bitmap scaled = null;
                try
                {
                    if (_watchMaxWidth > 0 && bmp.Width > _watchMaxWidth)
                    {
                        double scale = (double)_watchMaxWidth / bmp.Width;
                        int nh = Math.Max(1, (int)Math.Round(bmp.Height * scale));
                        scaled = new Bitmap(_watchMaxWidth, nh, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(scaled))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(bmp, 0, 0, _watchMaxWidth, nh);
                        }
                        save = scaled;
                    }
                    SaveJpeg(save, path, 72);
                }
                finally { if (scaled != null) scaled.Dispose(); }

                IntPtr fg = Native.GetForegroundWindow();
                Dictionary<string, object> frame = new Dictionary<string, object>();
                DateTime now = DateTime.Now;
                frame["t"] = now.ToString("HH:mm:ss.fff");
                frame["wall"] = now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                frame["path"] = path;
                frame["file"] = string.Format("frames/f{0:D4}.jpg", _watchSeq);
                frame["ratio"] = diff != null ? diff["ratio"] : 0.0;
                frame["bbox"] = diff != null ? diff["bbox"] : null;
                frame["title"] = InputLog.WindowTitle(fg);
                string exe = InputLog.ExeOf(fg);
                if (exe != null) frame["exe"] = exe;
                lock (_watchSync)
                {
                    _watchFrames.Add(frame);
                    while (_watchFrames.Count > 400)
                    {
                        Dictionary<string, object> old = _watchFrames[0];
                        _watchFrames.RemoveAt(0);
                        try
                        {
                            string oldPath = Convert.ToString(old["path"]);
                            if (File.Exists(oldPath)) File.Delete(oldPath);
                        }
                        catch { }
                    }
                }
                _lastKeptGrid = grid;
                _lastChangeAt = DateTime.Now;

                // 通知插件层：画面变了（浮窗显示 + 可选解说）
                try
                {
                    Dictionary<string, object> change = new Dictionary<string, object>();
                    change["ratio"] = frame["ratio"];
                    change["bbox"] = frame["bbox"];
                    change["title"] = frame["title"];
                    change["path"] = path;
                    change["file"] = frame["file"];
                    change["t"] = frame["t"];
                    Overlay.Raise("watch.change", change);
                }
                catch { }
            }
        }

        private object WatchStatus()
        {
            int count;
            lock (_watchSync) { count = _watchFrames.Count; }
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["running"] = _watchRun;
            res["frames"] = count;
            res["dir"] = _watchDir;
            res["fps"] = _watchFps;
            res["idleFps"] = _watchIdleFps;
            res["idleAfterMs"] = _watchIdleAfterMs;
            res["quietMs"] = _watchRun ? (long)(DateTime.Now - _lastChangeAt).TotalMilliseconds : 0;
            res["threshold"] = _watchThreshold;
            res["startedMs"] = _watchRun ? (long)(DateTime.Now - _watchStart).TotalMilliseconds : 0;
            return res;
        }

        private object WatchPeek(Dictionary<string, object> a)
        {
            int limit = Math.Max(1, Math.Min(10, Int(a, "limit", 3)));
            List<Dictionary<string, object>> frames = new List<Dictionary<string, object>>();
            lock (_watchSync)
            {
                int start = Math.Max(0, _watchFrames.Count - limit);
                frames.AddRange(_watchFrames.GetRange(start, _watchFrames.Count - start));
            }

            string latest = null;
            if (_watchDir != null)
            {
                latest = Path.Combine(_watchDir, "latest.jpg");
                try
                {
                    Rectangle vs = VirtualScreen();
                    using (Bitmap bmp = Capture(vs))
                    {
                        Bitmap save = bmp;
                        Bitmap scaled = null;
                        try
                        {
                            if (_watchMaxWidth > 0 && bmp.Width > _watchMaxWidth)
                            {
                                double scale = (double)_watchMaxWidth / bmp.Width;
                                int nh = Math.Max(1, (int)Math.Round(bmp.Height * scale));
                                scaled = new Bitmap(_watchMaxWidth, nh, PixelFormat.Format32bppArgb);
                                using (Graphics g = Graphics.FromImage(scaled))
                                {
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(bmp, 0, 0, _watchMaxWidth, nh);
                                }
                                save = scaled;
                            }
                            SaveJpeg(save, latest, 75);
                        }
                        finally { if (scaled != null) scaled.Dispose(); }
                    }
                }
                catch { latest = null; }
            }

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["running"] = _watchRun;
            res["frames"] = frames;
            res["latest"] = latest;
            res["inputs"] = InputLog.Tail(12);
            return res;
        }

        private object WatchSnapshot(Dictionary<string, object> a)
        {
            string path = Str(a, "path");
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("watch.snapshot 需要 path");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Rectangle vs = VirtualScreen();
            using (Bitmap bmp = Capture(vs))
                SaveJpeg(bmp, path, 75);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["path"] = path;
            res["w"] = vs.Width;
            res["h"] = vs.Height;
            return res;
        }

        private object WatchStop()
        {
            _watchRun = false;
            if (_watchThread != null)
            {
                try { _watchThread.Join(1500); } catch { }
                _watchThread = null;
            }
            _watcherStartedByLearn = false;
            int count;
            lock (_watchSync) { count = _watchFrames.Count; }
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["stopped"] = true;
            res["frames"] = count;
            res["dir"] = _watchDir;
            return res;
        }

        private void StopWatch()
        {
            _watchRun = false;
            if (_watchThread != null)
            {
                try { _watchThread.Join(1200); } catch { }
                _watchThread = null;
            }
        }

        // ---------- 学习 ----------

        private object LearnStart(Dictionary<string, object> a)
        {
            string dir = Str(a, "dir");
            if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("learn.start 需要 dir");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "frames"));
            InputLog.ClearSession();
            InputLog.SessionStart = DateTime.Now;
            InputLog.Learning = true;
            if (_watchRun && _watchDir != null && !string.Equals(Path.GetFullPath(_watchDir), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
            {
                WatchStop();
            }
            if (!_watchRun)
            {
                Dictionary<string, object> watchArgs = new Dictionary<string, object>();
                watchArgs["dir"] = dir;
                watchArgs["fps"] = Num(a, "fps", 2);
                watchArgs["threshold"] = Num(a, "threshold", 0.004);
                WatchStart(watchArgs);
                _watcherStartedByLearn = true;
            }
            Dictionary<string, object> meta = new Dictionary<string, object>();
            meta["name"] = Str(a, "name") ?? "未命名任务";
            meta["startedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            meta["startedMs"] = (long)(DateTime.Now - InputLog.SessionStart).TotalMilliseconds;
            File.WriteAllText(Path.Combine(dir, "meta.json"), _json.Serialize(meta), Encoding.UTF8);

            Dictionary<string, object> res = new Dictionary<string, object>();
            res["recording"] = true;
            res["dir"] = dir;
            res["startedAt"] = meta["startedAt"];
            res["watching"] = _watchRun;
            return res;
        }

        private object LearnStatus()
        {
            int session;
            lock (InputLog.Sync) { session = InputLog.Session.Count; }
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["recording"] = InputLog.Learning;
            res["inputs"] = session;
            res["elapsedMs"] = InputLog.Learning ? (long)(DateTime.Now - InputLog.SessionStart).TotalMilliseconds : 0;
            res["watching"] = _watchRun;
            res["dir"] = _watchDir;
            return res;
        }

        private object LearnStop()        {
            InputLog.Learning = false;
            DateTime end = DateTime.Now;
            List<Dictionary<string, object>> events = InputLog.SessionCopy();
            List<Dictionary<string, object>> frames = new List<Dictionary<string, object>>();
            lock (_watchSync) { frames.AddRange(_watchFrames); }
            string dir = _watchDir;
            if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("没有正在录制的会话");
            Directory.CreateDirectory(dir);

            StringBuilder jsonl = new StringBuilder();
            foreach (Dictionary<string, object> ev in events)
                jsonl.AppendLine(_json.Serialize(ev));
            File.WriteAllText(Path.Combine(dir, "events.jsonl"), jsonl.ToString(), Encoding.UTF8);

            StringBuilder md = new StringBuilder();
            md.AppendLine("# 学习记录 " + InputLog.SessionStart.ToString("yyyy-MM-dd HH:mm:ss"));
            md.AppendLine();
            md.AppendLine("- 时长：" + Math.Round((end - InputLog.SessionStart).TotalSeconds, 1) + " 秒");
            md.AppendLine("- 输入事件：" + events.Count + " 个");
            md.AppendLine("- 画面变化帧：" + frames.Count + " 张");
            md.AppendLine();
            md.AppendLine("## 时间线");
            md.AppendLine();

            List<KeyValuePair<DateTime, string>> lines = new List<KeyValuePair<DateTime, string>>();
            foreach (Dictionary<string, object> ev in events)
            {
                DateTime t;
                try { t = DateTime.Parse(Convert.ToString(ev["wall"])); }
                catch { t = DateTime.Now; }
                lines.Add(new KeyValuePair<DateTime, string>(t, EventLine(ev)));
            }
            foreach (Dictionary<string, object> fr in frames)
            {
                DateTime t;
                try { t = DateTime.Parse(Convert.ToString(fr["wall"])); }
                catch { t = DateTime.Now; }
                lines.Add(new KeyValuePair<DateTime, string>(t, FrameLine(fr)));
            }
            lines.Sort(delegate(KeyValuePair<DateTime, string> x, KeyValuePair<DateTime, string> y) { return x.Key.CompareTo(y.Key); });
            foreach (KeyValuePair<DateTime, string> kv in lines) md.AppendLine("- " + kv.Value);

            string timelinePath = Path.Combine(dir, "timeline.md");
            File.WriteAllText(timelinePath, md.ToString(), Encoding.UTF8);

            Dictionary<string, object> summary = new Dictionary<string, object>();
            summary["dir"] = dir;
            summary["timeline"] = timelinePath;
            summary["events"] = events.Count;
            summary["frames"] = frames.Count;
            summary["durationMs"] = (long)(end - InputLog.SessionStart).TotalMilliseconds;
            List<Dictionary<string, object>> sample = new List<Dictionary<string, object>>();
            int from = Math.Max(0, frames.Count - 6);
            for (int i = from; i < frames.Count; i++) sample.Add(frames[i]);
            summary["sampleFrames"] = sample;

            if (_watcherStartedByLearn)
            {
                WatchStop();
                _watcherStartedByLearn = false;
            }
            return summary;
        }

        private object OverlayConfigure(Dictionary<string, object> a)
        {
            bool enabled = Bool(a, "enabled", true);
            string position = Str(a, "position");
            if (string.IsNullOrEmpty(position)) position = "top-center";
            bool locked = Bool(a, "locked", false);
            int x = Int(a, "x", 0);
            int y = Int(a, "y", 0);
            int width = Int(a, "width", 460);
            double opacity = Num(a, "opacity", 0.85);
            Overlay.Configure(enabled, position, x, y, locked, width, opacity);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["enabled"] = enabled;
            res["position"] = position;
            res["locked"] = locked;
            res["width"] = Math.Max(300, Math.Min(900, width));
            res["opacity"] = Math.Round(Math.Max(0.3, Math.Min(1.0, opacity <= 0 ? 0.85 : opacity)), 2);
            res["created"] = Overlay.Created;
            if (Overlay.LastError != null) res["lastError"] = Overlay.LastError;
            return res;
        }

        private object OverlayChatLog(Dictionary<string, object> a)
        {
            string line = Str(a, "line");
            if (string.IsNullOrEmpty(line)) line = Str(a, "text");
            if (!string.IsNullOrEmpty(line)) Overlay.ChatLog(line);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["ok"] = true;
            return res;
        }

        private object OverlayAlert(Dictionary<string, object> a)
        {
            string text = Str(a, "text");
            if (string.IsNullOrEmpty(text)) text = "停止操作：用户优先";
            int durationMs = Math.Max(600, Math.Min(60000, Int(a, "durationMs", 2200)));
            Overlay.Alert(text, durationMs);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["text"] = text;
            res["durationMs"] = durationMs;
            return res;
        }

        private object OverlayPreview(Dictionary<string, object> a)
        {
            int durationMs = Int(a, "durationMs", 5000);
            Overlay.Preview(durationMs);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["previewing"] = true;
            res["durationMs"] = durationMs;
            return res;
        }

        private object OverlayStatus(){
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["created"] = Overlay.Created;
            res["visible"] = Overlay.Visible;
            res["bounds"] = Overlay.Bounds;
            res["text"] = Overlay.StatusText;
            res["chat"] = Overlay.ChatText;
            if (Overlay.LastError != null) res["lastError"] = Overlay.LastError;
            return res;
        }

        private object OverlayState(Dictionary<string, object> a)
        {
            string mode = Str(a, "mode");
            if (string.IsNullOrEmpty(mode)) mode = "idle";
            string label = Str(a, "label");
            if (string.IsNullOrEmpty(label)) label = "待机";
            string action = Str(a, "action");
            if (action == null) action = "";
            bool userPriority = Bool(a, "userPriority", true);
            bool paused = Bool(a, "paused", false);
            int delay = Int(a, "delayHideMs", 2000);
            Overlay.State(mode, label, action, delay, userPriority, paused);
            Dictionary<string, object> res = new Dictionary<string, object>();
            res["mode"] = mode;
            res["label"] = label;
            res["action"] = action;
            res["userPriority"] = userPriority;
            res["paused"] = paused;
            return res;
        }

        private static string EventLine(Dictionary<string, object> ev)
        {
            string t = Convert.ToString(ev["t"]);
            string kind = Convert.ToString(ev["kind"]);
            string action = ev.ContainsKey("action") ? Convert.ToString(ev["action"]) : "";
            string title = ev.ContainsKey("title") ? Convert.ToString(ev["title"]) : "";
            string exe = ev.ContainsKey("exe") ? Convert.ToString(ev["exe"]) : "";
            string win = (title ?? "").Length > 0 ? (exe != null && exe.Length > 0 ? title + " (" + exe + ")" : title) : "";
            if (kind == "mouse" && action == "click")
            {
                string button = ev.ContainsKey("button") ? Convert.ToString(ev["button"]) : "left";
                return string.Format("[{0}] 鼠标点击 {1} ({2}, {3}) — 窗口：{4}",
                    t, button, ev["x"], ev["y"], win);
            }
            if (kind == "mouse" && action == "wheel")
            {
                return string.Format("[{0}] 滚轮 {1} ({2}, {3}) — 窗口：{4}",
                    t, ev.ContainsKey("wheel") ? ev["wheel"] : "", ev["x"], ev["y"], win);
            }
            if (kind == "key")
            {
                string key = ev.ContainsKey("key") ? Convert.ToString(ev["key"]) : ("vk" + ev["vk"]);
                string mods = ev.ContainsKey("mods") ? Convert.ToString(ev["mods"]) : "";
                string combo = mods != null && mods.Length > 0 ? mods + "+" + key : key;
                return string.Format("[{0}] 按键 {1} — 窗口：{2}", t, combo, win);
            }
            return string.Format("[{0}] {1}", t, kind);
        }

        private static string FrameLine(Dictionary<string, object> fr)
        {
            string t = Convert.ToString(fr["t"]);
            string file = Convert.ToString(fr["file"]);
            object ratio = fr.ContainsKey("ratio") ? fr["ratio"] : 0;
            string title = Convert.ToString(fr["title"]);
            object bboxObj = fr.ContainsKey("bbox") ? fr["bbox"] : null;
            string box = "";
            Dictionary<string, object> bbox = bboxObj as Dictionary<string, object>;
            if (bbox != null)
                box = string.Format(" 区域 ({0},{1} {2}x{3})", bbox["x"], bbox["y"], bbox["w"], bbox["h"]);
            return string.Format("[{0}] 画面变化 {1}%{2} — 截图 {3} — 窗口：{4}",
                t, Math.Round(Convert.ToDouble(ratio) * 100, 1), box, file, title);
        }

        // ---------- 参数工具 ----------

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return null;
        }

        private static int Int(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return ToInt(v, def);
            return def;
        }

        private static double Num(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToDouble(v); } catch { return def; }
            }
            return def;
        }

        private static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToBoolean(v); } catch { return def; }
            }
            return def;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
                return v as Dictionary<string, object>;
            return null;
        }

        private static List<Rectangle> RegionsArg(Dictionary<string, object> a)
        {
            List<Rectangle> list = new List<Rectangle>();
            object v;
            if (a == null || !a.TryGetValue("regions", out v) || v == null) return list;
            object[] arr = v as object[];
            if (arr == null) return list;
            foreach (object o in arr)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                int w = Int(d, "w", 0);
                int h = Int(d, "h", 0);
                if (w <= 0 || h <= 0) continue;
                list.Add(new Rectangle(Int(d, "x", 0), Int(d, "y", 0), w, h));
                if (list.Count >= 4) break;
            }
            return list;
        }

        private static string[] StrArray(Dictionary<string, object> d, string key)        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return new string[0];
            object[] arr = v as object[];
            if (arr == null) return new string[0];
            List<string> list = new List<string>();
            foreach (object o in arr) if (o != null) list.Add(Convert.ToString(o));
            return list.ToArray();
        }

        private static int ToInt(object v, int def)
        {
            try { return Convert.ToInt32(v); } catch { return def; }
        }
    }

    internal static class Program
    {
        private static void Main(string[] rawArgs)
        {
            AppDomain.CurrentDomain.FirstChanceException += delegate(object sender, FirstChanceExceptionEventArgs e)
            {
                if (e.Exception is NullReferenceException)
                {
                    try
                    {
                        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cch-crash.log");
                        Exception ex = e.Exception;
                        string stack = "?";
                        try { stack = ex.StackTrace ?? "(no stack)"; } catch { }
                        System.IO.File.AppendAllText(path, string.Format("{0} [FirstChance] {1}\n{2}\n\n", DateTime.Now.ToString("HH:mm:ss.fff"), ex.GetType().FullName, stack));
                    }
                    catch { }
                }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                CrashLog("AppDomain", e.ExceptionObject);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                CrashLog("UIThread", e.Exception);
            };
            Server server = new Server();
            server.Run();
        }

        private static void CrashLog(string where, object error)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cch-crash.log");
                string type = "-";
                string stack = "-";
                System.Exception ex = error as System.Exception;
                if (ex != null)
                {
                    try { type = ex.GetType().FullName; } catch { }
                    try { stack = ex.StackTrace ?? "(no stack)"; } catch { }
                }
                string text = string.Format("{0} [{1}] {2}\n{3}\n\n", DateTime.Now.ToString("HH:mm:ss.fff"), where, type, stack);
                System.IO.File.AppendAllText(path, text);
                Console.Error.WriteLine("CRASH " + where + ": " + type);
            }
            catch { }
        }
    }
}
