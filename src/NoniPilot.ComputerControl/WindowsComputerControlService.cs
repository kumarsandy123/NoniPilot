using System.Text;
using NoniPilot.Domain.Interfaces;
using static NoniPilot.ComputerControl.NativeMethods;

namespace NoniPilot.ComputerControl;

/// <summary>
/// Real mouse/keyboard/window control via Win32 SendInput (section 7: "Windows SendInput and
/// controlled native APIs"). EmergencyStop is synchronous and lock-free by design - it must
/// work even if the calling agent loop is deadlocked, per section 10/21.4.
/// </summary>
public sealed class WindowsComputerControlService : IComputerControlService
{
    // Common SendKeys-style special-key names -> virtual key codes. Deliberately a small,
    // extensible subset (section 8.4's "keyboard shortcuts") rather than the full SendKeys grammar.
    private static readonly Dictionary<string, ushort> SpecialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTER"] = 0x0D,
        ["TAB"] = 0x09,
        ["ESC"] = 0x1B,
        ["ESCAPE"] = 0x1B,
        ["BACKSPACE"] = 0x08,
        ["DELETE"] = 0x2E,
        ["DEL"] = 0x2E,
        ["HOME"] = 0x24,
        ["END"] = 0x23,
        ["PGUP"] = 0x21,
        ["PGDN"] = 0x22,
        ["UP"] = 0x26,
        ["DOWN"] = 0x28,
        ["LEFT"] = 0x25,
        ["RIGHT"] = 0x27,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
    };

    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12; // Alt
    private const ushort VkShift = 0x10;

    /// <summary>
    /// Flipped to true the instant EmergencyStop() runs. Checked between every incremental
    /// step of a drag/type/click sequence so a long-running action aborts within one step,
    /// not at the end of it. Reset back to false at the start of each new top-level action.
    /// </summary>
    private volatile bool _stopRequested;

    public Task MoveMouseAsync(int x, int y, CancellationToken cancellationToken = default)
    {
        BeginAction();
        SetCursorPos(x, y);
        return Task.CompletedTask;
    }

    public Task ClickAsync(int x, int y, MouseButton button = MouseButton.Left, int clickCount = 1, CancellationToken cancellationToken = default)
    {
        BeginAction();
        SetCursorPos(x, y);

        var (down, up) = ButtonFlags(button);

        for (var i = 0; i < clickCount && !_stopRequested; i++)
        {
            SendMouseButton(down);
            SendMouseButton(up);
        }

        return Task.CompletedTask;
    }

    public async Task DragAsync(int fromX, int fromY, int toX, int toY, CancellationToken cancellationToken = default)
    {
        BeginAction();
        SetCursorPos(fromX, fromY);
        SendMouseButton(MouseEventLeftDown);

        const int steps = 20;
        try
        {
            for (var i = 1; i <= steps; i++)
            {
                if (_stopRequested || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var x = fromX + (toX - fromX) * i / steps;
                var y = fromY + (toY - fromY) * i / steps;
                SetCursorPos(x, y);
                await Task.Delay(8, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Always release the button we pressed, even if the drag was interrupted.
            SendMouseButton(MouseEventLeftUp);
        }
    }

    public void PressMouseButton(MouseButton button = MouseButton.Left)
    {
        BeginAction();
        SendMouseButton(ButtonFlags(button).Down);
    }

    public void ReleaseMouseButton(MouseButton button = MouseButton.Left)
    {
        SendMouseButton(ButtonFlags(button).Up);
    }

    private static (uint Down, uint Up) ButtonFlags(MouseButton button) => button switch
    {
        MouseButton.Left => (MouseEventLeftDown, MouseEventLeftUp),
        MouseButton.Right => (MouseEventRightDown, MouseEventRightUp),
        MouseButton.Middle => (MouseEventMiddleDown, MouseEventMiddleUp),
        _ => (MouseEventLeftDown, MouseEventLeftUp),
    };

    public Task ScrollAsync(int deltaWheelClicks, CancellationToken cancellationToken = default)
    {
        BeginAction();
        const int wheelDelta = 120;
        SendMouseEvent(MouseEventWheel, (uint)(deltaWheelClicks * wheelDelta));
        return Task.CompletedTask;
    }

    public async Task TypeTextAsync(string text, CancellationToken cancellationToken = default)
    {
        BeginAction();

        foreach (var ch in text)
        {
            if (_stopRequested || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SendUnicodeChar(ch, keyUp: false);
            SendUnicodeChar(ch, keyUp: true);
            await Task.Delay(4, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task SendKeysAsync(string keys, CancellationToken cancellationToken = default)
    {
        BeginAction();

        var modifiers = new List<ushort>();
        var i = 0;
        while (i < keys.Length && (keys[i] == '^' || keys[i] == '%' || keys[i] == '+'))
        {
            modifiers.Add(keys[i] switch
            {
                '^' => VkControl,
                '%' => VkMenu,
                '+' => VkShift,
                _ => VkControl,
            });
            i++;
        }

        ushort mainKey;
        if (i < keys.Length && keys[i] == '{' && keys.IndexOf('}', i) is var close && close > i)
        {
            var name = keys.Substring(i + 1, close - i - 1);
            mainKey = SpecialKeys.TryGetValue(name, out var vk) ? vk : (ushort)0;
        }
        else if (i < keys.Length)
        {
            mainKey = (ushort)char.ToUpperInvariant(keys[i]);
        }
        else
        {
            return Task.CompletedTask;
        }

        foreach (var m in modifiers)
        {
            SendVirtualKey(m, keyUp: false);
        }

        SendVirtualKey(mainKey, keyUp: false);
        SendVirtualKey(mainKey, keyUp: true);

        for (var m = modifiers.Count - 1; m >= 0; m--)
        {
            SendVirtualKey(modifiers[m], keyUp: true);
        }

        return Task.CompletedTask;
    }

    public Task<bool> FocusWindowAsync(string windowTitleContains, CancellationToken cancellationToken = default)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            var length = GetWindowTextLength(hWnd);
            if (length == 0)
            {
                return true;
            }

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);

            if (sb.ToString().Contains(windowTitleContains, StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false; // stop enumerating
            }

            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(SetForegroundWindow(found));
    }

    public void EmergencyStop()
    {
        _stopRequested = true;

        // Belt-and-suspenders: unconditionally release any button that might be held down
        // by an in-flight DragAsync, regardless of which thread it's running on.
        SendMouseButton(MouseEventLeftUp);
        SendMouseButton(MouseEventRightUp);
        SendMouseButton(MouseEventMiddleUp);
        SendVirtualKey(VkControl, keyUp: true);
        SendVirtualKey(VkMenu, keyUp: true);
        SendVirtualKey(VkShift, keyUp: true);
    }

    /// <summary>Every public action starts here so a fresh action isn't silently poisoned by a stale stop flag.</summary>
    private void BeginAction() => _stopRequested = false;

    private static void SendMouseButton(uint flag) => SendMouseEvent(flag, 0);

    private static void SendMouseEvent(uint flags, uint mouseData)
    {
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInput { Flags = flags, MouseData = mouseData },
            },
        };

        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Input>());
    }

    private static void SendUnicodeChar(char ch, bool keyUp)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    ScanCode = ch,
                    Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                },
            },
        };

        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Input>());
    }

    private static void SendVirtualKey(ushort virtualKey, bool keyUp)
    {
        if (virtualKey == 0)
        {
            return;
        }

        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0,
                },
            },
        };

        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Input>());
    }
}
