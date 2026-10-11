using System.Runtime.InteropServices;
using System.Windows.Input;

namespace UfoEncounter.Util;

/// <summary>
/// System-wide hotkeys via Win32 RegisterHotKey, so PANIK-STOP etc. work while
/// MSFS has the focus (e.g. in full screen). WM_HOTKEY arrives in the window's
/// message hook; call <see cref="Handle"/> from there.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5A10;

    public GlobalHotkeys(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>Register a hotkey; returns false if another program already owns it.</summary>
    public bool Register(ModifierKeys modifiers, Key key, Action action)
    {
        uint mods = MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods, (uint)KeyInterop.VirtualKeyFromKey(key))) return false;
        _actions[id] = action;
        return true;
    }

    /// <summary>Returns true if the message was one of our hotkeys.</summary>
    public bool Handle(int msg, IntPtr wParam)
    {
        if (msg != WM_HOTKEY) return false;
        if (_actions.TryGetValue(wParam.ToInt32(), out var action)) { action(); return true; }
        return false;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }
}
