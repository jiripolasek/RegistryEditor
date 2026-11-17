Add-Type @'
using System.Runtime.InteropServices;

public static class UxTestWindow {
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int Size, Flags; public nint Cursor; public int X, Y; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, nint name);
    public static bool HasVerticalResizeCursor() {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && info.Cursor == LoadCursor(0, 32645);
    }
    public static nint TargetWindow { get; set; }

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint key, nint data, uint flags, uint timeout, out nuint result);

    public static void AssertMenuChimeSuppressed(nint hwnd) {
        if (SendMessageTimeout(hwnd, 0x0120, 0x21, 0, 2, 1000, out var result) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (result != 0x10000)
            throw new System.InvalidOperationException("WM_MENUCHAR did not return MNC_CLOSE.");
        if (SendMessageTimeout(hwnd, 0x000e, 0, 0, 2, 1000, out result) == 0 || result == 0)
            throw new System.InvalidOperationException("The window stopped forwarding WM_GETTEXTLENGTH.");
    }

    public static async System.Threading.Tasks.Task<long> LongestWindowDelay(nint hwnd, System.Threading.CancellationToken stop) {
        long longest = 0;
        while (!stop.IsCancellationRequested) {
            await System.Threading.Tasks.Task.Delay(16).ConfigureAwait(false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (SendMessageTimeout(hwnd, 0, 0, 0, 1, 5000, out _) == 0) return 5000;
            longest = System.Math.Max(longest, watch.ElapsedMilliseconds);
        }
        return longest;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    public static void RequireForeground() {
        if (GetForegroundWindow() != TargetWindow) {
            SetForegroundWindow(TargetWindow);
            System.Threading.SpinWait.SpinUntil(() => GetForegroundWindow() == TargetWindow, 1000);
        }
        if (GetForegroundWindow() != TargetWindow) throw new System.InvalidOperationException("Windows did not grant foreground activation to the isolated test window.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input {
        public uint Type;
        public MouseInput Mouse;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint point);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X, Y; }
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(ScreenPoint point);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string name);
    [DllImport("user32.dll")]
    private static extern uint GetMenuState(nint menu, uint item, uint flags);

    public static string DescribeMouse(nint hwnd, int x, int y) {
        var point = new ScreenPoint { X = x, Y = y };
        var hit = WindowFromPoint(point);
        var actual = GetCursorPos(out var cursor);
        nint messagePoint = (y << 16) | (x & 65535);
        SendMessageTimeout(hwnd, 0x0084, 0, messagePoint, 2, 250, out var rootHit);
        SendMessageTimeout(hit, 0x0084, 0, messagePoint, 2, 250, out var childHit);
        return $"Expected {x},{y}, cursor {actual} at {cursor.X},{cursor.Y}, foreground {GetForegroundWindow()}, target {hwnd}, under point {hit}, hit tests {rootHit}/{childHit}.";
    }

    public static bool IsSystemMenuOpen(nint hwnd) {
        const uint getMenuHandle = 0x01e1, close = 0xf060, move = 0xf010;
        GetWindowThreadProcessId(hwnd, out var process);
        for (nint popup = FindWindowEx(0, 0, "#32768", null); popup != 0; popup = FindWindowEx(0, popup, "#32768", null)) {
            GetWindowThreadProcessId(popup, out var owner);
            if (owner == process &&
                SendMessageTimeout(popup, getMenuHandle, 0, 0, 2, 100, out var menu) != 0 &&
                GetMenuState((nint)menu, close, 0) != uint.MaxValue && GetMenuState((nint)menu, move, 0) != uint.MaxValue) return true;
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint dc, int x, int y);

    public static System.Threading.Tasks.Task<int[]> SampleRowText(int x, int y, int width, System.Threading.CancellationToken stop) {
        var dc = GetDC(0);
        var ink = new System.Collections.Generic.List<(int X, uint Color)>();
        try {
            var background = GetPixel(dc, x - 2, y);
            for (int i = 0; i < width && ink.Count < 3; i++) {
                var color = GetPixel(dc, x + i, y);
                if (ColorDistance(color, background) > 120) ink.Add((x + i, color));
            }
        } finally { ReleaseDC(0, dc); }
        if (ink.Count < 3) throw new System.InvalidOperationException("Could not locate unchanged text on the Sandbox desktop.");

        return System.Threading.Tasks.Task.Run(() => {
            var screen = GetDC(0);
            try {
                int frames = 0, changed = 0;
                do {
                    frames++;
                    if (ink.Exists(point => ColorDistance(GetPixel(screen, point.X, y), point.Color) > 60)) changed++;
                } while (!stop.WaitHandle.WaitOne(16));
                return new[] { frames, changed };
            } finally { ReleaseDC(0, screen); }
        });
    }

    private static int ColorDistance(uint a, uint b) =>
        System.Math.Abs((int)(a & 255) - (int)(b & 255)) +
        System.Math.Abs((int)(a >> 8 & 255) - (int)(b >> 8 & 255)) +
        System.Math.Abs((int)(a >> 16 & 255) - (int)(b >> 16 & 255));

    private static void MoveCursor(int x, int y) {
        if (SetCursorPos(x, y)) return;
        // SendInput takes absolute coordinates from 0 to 65535 over the entire virtual screen.
        const int virtualLeft = 76, virtualTop = 77, virtualWidth = 78, virtualHeight = 79;
        const uint moveAbsoluteOnVirtualDesktop = 0x0001 | 0x8000 | 0x4000;
        var move = new Input { Mouse = new MouseInput {
            X = (int)System.Math.Round((x - GetSystemMetrics(virtualLeft)) * 65535d / System.Math.Max(1, GetSystemMetrics(virtualWidth) - 1)),
            Y = (int)System.Math.Round((y - GetSystemMetrics(virtualTop)) * 65535d / System.Math.Max(1, GetSystemMetrics(virtualHeight) - 1)),
            Flags = moveAbsoluteOnVirtualDesktop
        } };
        if (SendInput(1, new[] { move }, Marshal.SizeOf<Input>()) != 1 ||
            !System.Threading.SpinWait.SpinUntil(() => GetCursorPos(out var point) && point.X == x && point.Y == y, 500))
            throw new System.InvalidOperationException("Windows did not move the cursor to the test window.");
    }

    private static void MoveToTestPoint(int x, int y) {
        RequireForeground();
        var point = new ScreenPoint { X = x, Y = y };
        var hit = GetAncestor(WindowFromPoint(point), 2);
        GetWindowThreadProcessId(hit, out var hitProcess);
        GetWindowThreadProcessId(TargetWindow, out var targetProcess);
        if (hit == 0 || hitProcess != targetProcess)
            throw new System.InvalidOperationException($"Another window covers the test target at {x},{y}.");
        MoveCursor(x, y);
        // Moving away from a tab can dismiss its tooltip before the click.
        if (!System.Threading.SpinWait.SpinUntil(() => GetAncestor(WindowFromPoint(point), 2) == TargetWindow, 5000))
            throw new System.InvalidOperationException($"A popup covers the test target at {x},{y}.");
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern int PostMessage(nint window, uint message, nuint key, nint state);
    public static void CloseWindow(nint window) {
        if (PostMessage(window, 0x0010, 0, 0) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public static void AltF4(nint window) {
        // Send the Alt+F4 system key to the test HWND without injecting a desktop-wide shortcut.
        if (PostMessage(window, 0x0104, 0x73, 0x203e0001) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    public static void ActivateAt(int x, int y) {
        if (GetForegroundWindow() == TargetWindow) return;
        SetForegroundWindow(TargetWindow);
        if (GetForegroundWindow() == TargetWindow) return;

        // Raise the isolated editor above Explorer until the activation click has landed.
        const uint keepBounds = 0x0013;
        if (!SetWindowPos(TargetWindow, -1, 0, 0, 0, 0, keepBounds))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not bring the test window to the top of its desktop.");
        try {
            var point = new ScreenPoint { X = x, Y = y };
            if (!System.Threading.SpinWait.SpinUntil(() => GetAncestor(WindowFromPoint(point), 2) == TargetWindow, 1500)) {
                var covering = GetAncestor(WindowFromPoint(point), 2);
                GetWindowThreadProcessId(covering, out var pid);
                var name = pid == 0 ? "desktop" : System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                throw new System.InvalidOperationException($"Window {covering} ({name}) covers the editor at {x},{y}.");
            }
            MoveCursor(x, y);
            if (GetAncestor(WindowFromPoint(point), 2) != TargetWindow)
                throw new System.InvalidOperationException("The test window moved before activation.");
            SendMouse(0, 0x0002, 0x0004);
            RequireForeground();
        } finally {
            SetWindowPos(TargetWindow, -2, 0, 0, 0, 0, keepBounds);
        }
    }

    public static void NavigationClick(uint button) => Click(button, 0x0080, 0x0100);
    public static void LeftClickAt(int x, int y) {
        MoveToTestPoint(x, y);
        Click(0, 0x0002, 0x0004);
    }
    public static void RightClickAt(int x, int y) {
        MoveToTestPoint(x, y);
        Click(0, 0x0008, 0x0010);
    }
    public static void DoubleClickAt(int x, int y) {
        MoveToTestPoint(x, y);
        Click(0, 0x0002, 0x0004);
        // Let the native menu open before the second click, within the double-click interval.
        System.Threading.Thread.Sleep(80);
        SendMouse(0, 0x0002, 0x0004);
    }
    public static void MiddleClickAt(int x, int y) {
        MoveToTestPoint(x, y);
        Click(0, 0x0020, 0x0040);
    }
    public static void MiddleClickAtOwnedPopup(int x, int y, nint popup) {
        const uint ownerWindow = 4;
        if (popup == 0 || GetWindow(popup, ownerWindow) != TargetWindow)
            throw new System.InvalidOperationException("The popup is not owned by the verified test window.");
        GetWindowThreadProcessId(popup, out var popupProcess);
        GetWindowThreadProcessId(TargetWindow, out var targetProcess);
        if (popupProcess == 0 || popupProcess != targetProcess)
            throw new System.InvalidOperationException("The popup does not belong to the verified test process.");
        if (GetForegroundWindow() != TargetWindow && GetForegroundWindow() != popup)
            throw new System.InvalidOperationException("Neither the test window nor its owned popup is foreground.");
        var point = new ScreenPoint { X = x, Y = y };
        if (GetAncestor(WindowFromPoint(point), 2) != popup)
            throw new System.InvalidOperationException($"Another window covers the owned popup target at {x},{y}.");
        MoveCursor(x, y);
        if (!System.Threading.SpinWait.SpinUntil(() => GetAncestor(WindowFromPoint(point), 2) == popup, 5000))
            throw new System.InvalidOperationException($"The popup target moved or was covered at {x},{y}.");
        if (GetForegroundWindow() != TargetWindow && GetForegroundWindow() != popup)
            throw new System.InvalidOperationException("Foreground changed away from the test window and its popup.");
        SendMouse(0, 0x0020, 0x0040);
    }
    private static void Click(uint data, uint downFlags, uint upFlags) {
        if (GetForegroundWindow() != TargetWindow) throw new System.InvalidOperationException("The isolated test window must be in the foreground for mouse input.");
        SendMouse(data, downFlags, upFlags);
    }
    private static void SendMouse(uint data, uint downFlags, uint upFlags) {
        var down = new Input { Type = 0, Mouse = new MouseInput { X = 0, Y = 0, Data = data, Flags = downFlags, Time = 0, ExtraInfo = 0 } };
        var up = down;
        up.Mouse.Flags = upFlags;
        var inputs = new[] { down, up };
        if (SendInput(2, inputs, Marshal.SizeOf<Input>()) != 2)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Mouse input was not accepted.");
    }
}
'@

function Get-TestActivationPoint {
    $items = Read-Ui
    $add = @($items | Where-Object automationId -eq 'AddButton')[0]
    $minimize = @($items | Where-Object automationId -eq 'Minimize')[0]
    $titleBar = @($items | Where-Object automationId -eq 'RegFileTitleBar')[0]
    if ($titleBar -and $minimize) {
        return @{
            X = [int][Math]::Min($minimize.x - 24, $titleBar.x + $titleBar.width / 2)
            Y = [int]($titleBar.y + [Math]::Min($titleBar.height, $minimize.height) / 2)
        }
    }
    if (-not $add -or -not $minimize -or $add.x + $add.width -ge $minimize.x) { throw 'The window has no visible title-bar drag area.' }
    @{ X = [int](($add.x + $add.width + $minimize.x) / 2); Y = [int]($minimize.y + $minimize.height / 2) }
}

function Set-TestWindowForeground {
    if ([UxTestWindow]::GetForegroundWindow() -eq [UxTestWindow]::TargetWindow) { return }
    $point = Get-TestActivationPoint
    [UxTestWindow]::ActivateAt($point.X, $point.Y)
}
