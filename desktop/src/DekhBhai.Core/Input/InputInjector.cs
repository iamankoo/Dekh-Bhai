using System.Runtime.InteropServices;
using DekhBhai.Core.Rtc;

namespace DekhBhai.Core.Input;

/// <summary>
/// Low-level Windows input injection using SendInput API.
/// Translates normalized coordinates and key codes to Windows INPUT structures.
/// </summary>
public sealed class InputInjector : IDisposable
{
    private readonly int _screenWidth;
    private readonly int _screenHeight;
    private readonly HashSet<ushort> _pressedKeys = new();
    private readonly object _lock = new();

    // Virtual key codes
    private static readonly Dictionary<string, ushort> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Letters
        ["a"] = 0x41, ["b"] = 0x42, ["c"] = 0x43, ["d"] = 0x44, ["e"] = 0x45, ["f"] = 0x46,
        ["g"] = 0x47, ["h"] = 0x48, ["i"] = 0x49, ["j"] = 0x4A, ["k"] = 0x4B, ["l"] = 0x4C,
        ["m"] = 0x4D, ["n"] = 0x4E, ["o"] = 0x4F, ["p"] = 0x50, ["q"] = 0x51, ["r"] = 0x52,
        ["s"] = 0x53, ["t"] = 0x54, ["u"] = 0x55, ["v"] = 0x56, ["w"] = 0x57, ["x"] = 0x58,
        ["y"] = 0x59, ["z"] = 0x5A,

        // Numbers
        ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
        ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,

        // Function keys
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74,
        ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79,
        ["f11"] = 0x7A, ["f12"] = 0x7B,

        // Special keys
        ["enter"] = 0x0D, ["escape"] = 0x1B, ["esc"] = 0x1B, ["tab"] = 0x09,
        ["backspace"] = 0x08, ["space"] = 0x20, [" "] = 0x20,
        ["shift"] = 0x10, ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12,
        ["meta"] = 0x5B, ["win"] = 0x5B, ["super"] = 0x5B,
        ["capslock"] = 0x14, ["caps"] = 0x14,

        // Arrows
        ["arrowleft"] = 0x25, ["left"] = 0x25,
        ["arrowup"] = 0x26, ["up"] = 0x26,
        ["arrowright"] = 0x27, ["right"] = 0x27,
        ["arrowdown"] = 0x28, ["down"] = 0x28,

        // Navigation
        ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["insert"] = 0x2D, ["delete"] = 0x2E,

        // Punctuation
        [";"] = 0xBA, [":"] = 0xBA,
        ["="] = 0xBB, ["+"] = 0xBB,
        [","] = 0xBC, ["<"] = 0xBC,
        ["-"] = 0xBD, ["_"] = 0xBD,
        ["."] = 0xBE, [">"] = 0xBE,
        ["/"] = 0xBF, ["?"] = 0xBF,
        ["`"] = 0xC0, ["~"] = 0xC0,
        ["["] = 0xDB, ["{"] = 0xDB,
        ["\\"] = 0xDC, ["|"] = 0xDC,
        ["]"] = 0xDD, ["}"] = 0xDD,
        ["'"] = 0xDE, ["\""] = 0xDE,
    };

    public InputInjector(int screenWidth, int screenHeight)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    public void InjectMouseMove(double normalizedX, double normalizedY)
    {
        lock (_lock)
        {
            int x = (int)(normalizedX * 65535);
            int y = (int)(normalizedY * 65535);

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = x,
                        dy = y,
                        mouseData = 0,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInput(1, ref input, Marshal.SizeOf<INPUT>());
        }
    }

    public void InjectMouseDown(string button)
    {
        lock (_lock)
        {
            uint flags = button.ToLowerInvariant() switch
            {
                "left" => MOUSEEVENTF_LEFTDOWN,
                "right" => MOUSEEVENTF_RIGHTDOWN,
                "middle" => MOUSEEVENTF_MIDDLEDOWN,
                _ => 0
            };

            if (flags != 0)
            {
                var input = new INPUT
                {
                    type = INPUT_MOUSE,
                    U = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = 0, dy = 0, mouseData = 0,
                            dwFlags = flags | MOUSEEVENTF_ABSOLUTE,
                            time = 0, dwExtraInfo = IntPtr.Zero
                        }
                    }
                };
                SendInput(1, ref input, Marshal.SizeOf<INPUT>());
            }
        }
    }

    public void InjectMouseUp(string button)
    {
        lock (_lock)
        {
            uint flags = button.ToLowerInvariant() switch
            {
                "left" => MOUSEEVENTF_LEFTUP,
                "right" => MOUSEEVENTF_RIGHTUP,
                "middle" => MOUSEEVENTF_MIDDLEUP,
                _ => 0
            };

            if (flags != 0)
            {
                var input = new INPUT
                {
                    type = INPUT_MOUSE,
                    U = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = 0, dy = 0, mouseData = 0,
                            dwFlags = flags | MOUSEEVENTF_ABSOLUTE,
                            time = 0, dwExtraInfo = IntPtr.Zero
                        }
                    }
                };
                SendInput(1, ref input, Marshal.SizeOf<INPUT>());
            }
        }
    }

    public void InjectMouseClick(string button)
    {
        InjectMouseDown(button);
        Thread.Sleep(10);
        InjectMouseUp(button);
    }

    public void InjectMouseDoubleClick()
    {
        InjectMouseClick("left");
        Thread.Sleep(50);
        InjectMouseClick("left");
    }

    public void InjectScroll(double deltaY)
    {
        lock (_lock)
        {
            int scrollAmount = (int)(deltaY * 120);
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0, dy = 0, mouseData = (uint)scrollAmount,
                        dwFlags = MOUSEEVENTF_WHEEL | MOUSEEVENTF_ABSOLUTE,
                        time = 0, dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInput(1, ref input, Marshal.SizeOf<INPUT>());
        }
    }

    public void InjectKeyDown(string key, KeyboardModifiers? modifiers = null)
    {
        lock (_lock)
        {
            ApplyModifiers(modifiers, true);

            if (KeyMap.TryGetValue(key.ToLowerInvariant(), out var vk))
            {
                if (!_pressedKeys.Contains(vk))
                {
                    _pressedKeys.Add(vk);
                    SendKey(vk, false);
                }
            }
            else if (key.Length == 1)
            {
                SendChar(key[0]);
            }
        }
    }

    public void InjectKeyUp(string key, KeyboardModifiers? modifiers = null)
    {
        lock (_lock)
        {
            if (KeyMap.TryGetValue(key.ToLowerInvariant(), out var vk))
            {
                _pressedKeys.Remove(vk);
                SendKey(vk, true);
            }

            ApplyModifiers(modifiers, false);
        }
    }

    public void InjectText(string text)
    {
        lock (_lock)
        {
            foreach (char c in text)
            {
                SendChar(c);
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>
    /// Releases every key and mouse button this injector may have pressed. Called whenever a
    /// controller disconnects (clean or abrupt) or control is revoked, so a viewer that vanishes
    /// mid-gesture (network drop, app killed, browser tab closed) can never leave the host's
    /// Ctrl/Shift/Alt/Win or a mouse button stuck down.
    /// </summary>
    public void ReleaseAllKeys()
    {
        lock (_lock)
        {
            foreach (var vk in _pressedKeys.ToArray())
            {
                SendKey(vk, true);
            }
            _pressedKeys.Clear();

            SendKey(0x10, true); // Shift
            SendKey(0x11, true); // Ctrl
            SendKey(0x12, true); // Alt
            SendKey(0x5B, true); // Win

            // Unconditional mouse-up for every button - a no-op if the button wasn't actually
            // down, but the only way to guarantee none is left held after an abrupt disconnect.
            SendMouseUpRaw(MOUSEEVENTF_LEFTUP);
            SendMouseUpRaw(MOUSEEVENTF_RIGHTUP);
            SendMouseUpRaw(MOUSEEVENTF_MIDDLEUP);
        }
    }

    private static void SendMouseUpRaw(uint flag)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0, dy = 0, mouseData = 0,
                    dwFlags = flag | MOUSEEVENTF_ABSOLUTE,
                    time = 0, dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    private void ApplyModifiers(KeyboardModifiers? modifiers, bool down)
    {
        if (modifiers == null) return;

        if (modifiers.Shift) SendKey(0x10, !down);
        if (modifiers.Ctrl) SendKey(0x11, !down);
        if (modifiers.Alt) SendKey(0x12, !down);
        if (modifiers.Meta) SendKey(0x5B, !down);
    }

    private void SendKey(ushort vk, bool up)
    {
        // KEYEVENTF_UNICODE is documented to require wVk == 0 (it tells Windows to synthesize a
        // VK_PACKET keystroke from wScan instead of using a real virtual-key code). This used to
        // set KEYEVENTF_UNICODE together with a real, non-zero vk - undefined behavior per the
        // SendInput contract - which is exactly why special keys sent through this path (Enter,
        // Backspace, arrows, modifiers, ...) were flaky/silently dropped in some target
        // applications during testing, while SendChar's pure-Unicode path (wVk=0) worked fine.
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = up ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    private void SendChar(char c)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = KEYEVENTF_UNICODE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, ref input, Marshal.SizeOf<INPUT>());

        input.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        ReleaseAllKeys();
    }

    // Win32 P/Invoke
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);
}