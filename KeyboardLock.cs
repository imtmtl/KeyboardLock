// KeyboardLock - 단축키로 키보드 입력을 잠그고, 트레이 아이콘으로 상태를 표시합니다.
// 빌드: build.bat (Windows 기본 .NET Framework 4.x 의 csc.exe 사용, C# 5 문법)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("KeyboardLock")]
[assembly: System.Reflection.AssemblyProduct("KeyboardLock")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace KeyboardLock
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "KeyboardLock_SingleInstance_7f3c2a", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("KeyboardLock이 이미 실행 중입니다.\n화면 오른쪽 아래 트레이 아이콘을 확인하세요.",
                        "KeyboardLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        // 잠금/해제 단축키: Ctrl + Alt + Shift + K (같은 조합으로 토글)
        //  - 세 줄(맨 아랫줄 Ctrl/Alt, Shift 줄, 가운데 줄 K)에 걸쳐 있고 서로 떨어져 있어
        //    책등처럼 한 줄로 눌리는 물체로는 동시에 누를 수 없습니다.
        //  - 추가로 "정확히 이 4개 키만" 눌려 있을 때만 인정하므로, 다른 키가 하나라도
        //    함께 눌려 있으면(책이 여러 키를 누른 상태) 무시됩니다.
        //  - Windows/주요 프로그램의 기본 단축키와 겹치지 않으며, 인식되면 K 입력은 삼켜서
        //    다른 프로그램으로 전달되지 않습니다.
        public const string HotkeyText = "Ctrl + Alt + Shift + K";
        const int TriggerVk = 0x4B; // 'K'

        const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
        const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
        const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        const int VK_MASK = 0xE8; // 할당되지 않은 가상키 (Alt 메뉴 활성화/입력언어 전환 방지용)

        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "KeyboardLock";

        readonly NotifyIcon tray;
        readonly ToolStripMenuItem toggleItem;
        readonly ToolStripMenuItem autoStartItem;
        readonly Icon lockedIcon, unlockedIcon;
        readonly OsdForm osd = new OsdForm();
        readonly Control invoker = new Control();

        readonly NativeMethods.LowLevelKeyboardProc hookProc; // GC 방지용 참조 유지
        IntPtr hookHandle = IntPtr.Zero;

        volatile bool locked;
        // 물리적으로 눌려 있는 키 (vk -> 마지막 이벤트 시각)
        readonly Dictionary<int, DateTime> physicalDown = new Dictionary<int, DateTime>();
        // 시스템(다른 프로그램)이 눌린 상태로 알고 있는 키 = keydown 을 통과시킨 키
        readonly HashSet<int> systemDown = new HashSet<int>();

        public TrayApp()
        {
            invoker.CreateControl();
            lockedIcon = IconFactory.Create(true);
            unlockedIcon = IconFactory.Create(false);

            toggleItem = new ToolStripMenuItem("키보드 잠그기", null, delegate { SetLocked(!locked, true); });
            toggleItem.Font = new Font(toggleItem.Font, FontStyle.Bold);
            autoStartItem = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, delegate { ToggleAutoStart(); });
            autoStartItem.Checked = IsAutoStartEnabled();

            var hotkeyInfo = new ToolStripMenuItem("단축키: " + HotkeyText);
            hotkeyInfo.Enabled = false;

            var menu = new ContextMenuStrip();
            menu.Items.Add(toggleItem);
            menu.Items.Add(hotkeyInfo);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("종료", null, delegate { ExitThread(); }));

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) SetLocked(!locked, true);
            };

            hookProc = HookCallback;
            using (var proc = Process.GetCurrentProcess())
            using (var module = proc.MainModule)
            {
                hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, hookProc,
                    NativeMethods.GetModuleHandle(module.ModuleName), 0);
            }
            if (hookHandle == IntPtr.Zero)
            {
                MessageBox.Show("키보드 후킹을 설치하지 못했습니다. (오류 " + Marshal.GetLastWin32Error() + ")",
                    "KeyboardLock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            // 잠금 화면/사용자 전환 시 놓친 key-up 이벤트로 상태가 꼬이지 않도록 초기화
            SystemEvents.SessionSwitch += OnSessionSwitch;

            UpdateUi();
            tray.Visible = true;
            tray.ShowBalloonTip(3000, "KeyboardLock 실행됨",
                HotkeyText + " 로 키보드를 잠그고 풉니다.\n트레이 아이콘 클릭으로도 전환할 수 있습니다.",
                ToolTipIcon.Info);
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            lock (physicalDown)
            {
                physicalDown.Clear();
                systemDown.Clear();
            }
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                // 소프트웨어로 주입된 입력(화상 키보드, 원격 도구, 이 프로그램의 마스크 키)은 그대로 통과
                if ((info.flags & NativeMethods.LLKHF_INJECTED) == 0 && ProcessKey(wParam, (int)info.vkCode))
                    return (IntPtr)1; // 입력 차단
            }
            return NativeMethods.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        // true 를 반환하면 해당 키 이벤트를 차단
        bool ProcessKey(IntPtr wParam, int vk)
        {
            int msg = wParam.ToInt32();
            bool isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
            bool isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

            lock (physicalDown)
            {
                if (isDown)
                {
                    bool isRepeat = physicalDown.ContainsKey(vk);
                    physicalDown[vk] = DateTime.UtcNow;

                    if (vk == TriggerVk && !isRepeat && IsExactHotkey())
                    {
                        bool newState = !locked;
                        invoker.BeginInvoke((MethodInvoker)delegate { SetLocked(newState, true); });
                        locked = newState; // 이후 이벤트에 즉시 반영
                        SendMaskKey();
                        return true;
                    }

                    if (locked)
                        return true;

                    systemDown.Add(vk);
                    return false;
                }

                if (isUp)
                {
                    physicalDown.Remove(vk);
                    // 시스템이 눌린 것으로 알고 있는 키의 key-up 만 통과 → 잠금 전후로 키가 "눌린 채" 남지 않음
                    if (systemDown.Remove(vk))
                        return false;
                    return true;
                }
            }
            return locked;
        }

        // Ctrl, Alt, Shift 가 각각 하나씩 + K, 그 외 아무 키도 눌려 있지 않을 때만 true
        bool IsExactHotkey()
        {
            PruneStaleKeys();
            if (physicalDown.Count != 4) return false;
            bool ctrl = physicalDown.ContainsKey(VK_LCONTROL) || physicalDown.ContainsKey(VK_RCONTROL);
            bool alt = physicalDown.ContainsKey(VK_LMENU) || physicalDown.ContainsKey(VK_RMENU);
            bool shift = physicalDown.ContainsKey(VK_LSHIFT) || physicalDown.ContainsKey(VK_RSHIFT);
            return ctrl && alt && shift && physicalDown.ContainsKey(TriggerVk);
        }

        // 관리자 권한 창/보안 화면(Ctrl+Alt+Del 등) 때문에 key-up 을 놓쳐 남아 있는 항목 정리
        void PruneStaleKeys()
        {
            var now = DateTime.UtcNow;
            var stale = new List<int>();
            foreach (var kv in physicalDown)
            {
                if (kv.Key == TriggerVk) continue;
                bool systemSaysUp = systemDown.Contains(kv.Key) && (NativeMethods.GetAsyncKeyState(kv.Key) & 0x8000) == 0;
                if (systemSaysUp || (now - kv.Value).TotalSeconds > 30)
                    stale.Add(kv.Key);
            }
            foreach (int vk in stale)
            {
                physicalDown.Remove(vk);
                systemDown.Remove(vk);
            }
        }

        // 단축키의 K 를 삼킨 뒤 Ctrl/Alt/Shift 만 눌렸다 떼어진 것으로 보이면
        // Alt 메뉴 활성화나 Alt+Shift 입력언어 전환이 일어날 수 있어 무의미한 키를 끼워 넣음
        static void SendMaskKey()
        {
            var inputs = new NativeMethods.INPUT[2];
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].ki.wVk = VK_MASK;
            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].ki.wVk = VK_MASK;
            inputs[1].ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
            NativeMethods.SendInput(2, inputs, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
        }

        void SetLocked(bool value, bool showOsd)
        {
            locked = value;
            UpdateUi();
            if (showOsd) osd.ShowMessage(value);
        }

        void UpdateUi()
        {
            tray.Icon = locked ? lockedIcon : unlockedIcon;
            // 툴팁은 63자 제한
            tray.Text = (locked ? "KeyboardLock - 잠김" : "KeyboardLock - 해제") + "\n단축키: " + HotkeyText;
            toggleItem.Text = locked ? "키보드 잠금 해제" : "키보드 잠그기";
        }

        static bool IsAutoStartEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key != null && key.GetValue(RunValueName) != null;
            }
        }

        void ToggleAutoStart()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (IsAutoStartEnabled())
                    key.DeleteValue(RunValueName, false);
                else
                    key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
            }
            autoStartItem.Checked = IsAutoStartEnabled();
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(hookHandle);
                hookHandle = IntPtr.Zero;
            }
            tray.Visible = false;
            tray.Dispose();
            osd.Dispose();
            base.ExitThreadCore();
        }
    }

    // 잠금/해제 시 화면 하단에 잠깐 표시되는 안내 (포커스를 빼앗지 않음)
    sealed class OsdForm : Form
    {
        readonly Label label = new Label();
        readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();

        public OsdForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(300, 64);
            Opacity = 0.92;

            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.ForeColor = Color.White;
            label.Font = new Font("Malgun Gothic", 15f, FontStyle.Bold);
            Controls.Add(label);

            hideTimer.Interval = 1300;
            hideTimer.Tick += delegate { hideTimer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/ | 0x00000008 /*WS_EX_TOPMOST*/;
                return cp;
            }
        }

        public void ShowMessage(bool locked)
        {
            label.Text = locked ? "🔒  키보드 잠김" : "🔓  키보드 잠금 해제";
            BackColor = locked ? Color.FromArgb(200, 40, 40) : Color.FromArgb(30, 140, 70);
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 80);
            hideTimer.Stop();
            Show();
            hideTimer.Start();
        }
    }

    // 트레이 아이콘을 코드로 그림: 잠김 = 빨간 닫힌 자물쇠, 해제 = 초록 열린 자물쇠
    static class IconFactory
    {
        public static Icon Create(bool locked)
        {
            const int S = 32;
            using (var bmp = new Bitmap(S, S))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    Color body = locked ? Color.FromArgb(220, 45, 45) : Color.FromArgb(40, 170, 80);

                    // 고리(shackle)
                    using (var pen = new Pen(Color.FromArgb(235, 235, 235), 4.5f))
                    using (var outline = new Pen(Color.FromArgb(60, 60, 60), 7f))
                    {
                        var arcRect = locked ? new RectangleF(8, 2, 16, 18) : new RectangleF(15, 0, 16, 18);
                        foreach (var p in new[] { outline, pen })
                        {
                            g.DrawArc(p, arcRect, 180, 180);
                            if (locked)
                            {
                                g.DrawLine(p, 8, 11, 8, 16);
                                g.DrawLine(p, 24, 11, 24, 16);
                            }
                            else
                            {
                                g.DrawLine(p, 31, 9, 31, 16);
                            }
                        }
                    }

                    // 몸통
                    var bodyRect = new Rectangle(3, 14, 26, 17);
                    using (var path = RoundedRect(bodyRect, 4))
                    using (var brush = new SolidBrush(body))
                    using (var border = new Pen(Color.FromArgb(70, 0, 0, 0), 1.5f))
                    {
                        g.FillPath(brush, path);
                        g.DrawPath(border, path);
                    }

                    // 열쇠 구멍
                    using (var hole = new SolidBrush(Color.White))
                    {
                        g.FillEllipse(hole, 13, 18, 6, 6);
                        g.FillRectangle(hole, 14.75f, 22, 2.5f, 5);
                    }
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                NativeMethods.DestroyIcon(h);
                return icon;
            }
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    static class NativeMethods
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public const uint LLKHF_INJECTED = 0x10;
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki; // 64비트 기준 오프셋 (x64 로 빌드)
            [FieldOffset(8)] public MOUSEINPUT mi;  // union 크기를 맞추기 위한 필드
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
