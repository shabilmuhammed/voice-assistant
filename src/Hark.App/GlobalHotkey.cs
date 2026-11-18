using System.Runtime.InteropServices;
using System.Windows.Interop;
using Hark.Core;

namespace Hark.App;

/// <summary>System-wide keyboard shortcuts that work while MarTechChat is in the tray.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20, VkEscape = 0x1B;
    private const int QuickPromptId = 1, EscapeId = 2;
    private static readonly IntPtr MessageOnly = new(-3);

    private readonly HwndSource _source;
    private bool _escapeRegistered;

    /// <summary>Ctrl + Alt + Space.</summary>
    public event Action? QuickPrompt;
    public event Action? Escape;

    public bool QuickPromptAvailable { get; }

    public GlobalHotkey()
    {
        _source = new HwndSource(new HwndSourceParameters("HarkHotkeys") { ParentWindow = MessageOnly });
        _source.AddHook(Hook);
        // Ctrl + Alt + Space
        QuickPromptAvailable = RegisterHotKey(_source.Handle, QuickPromptId, ModControl | ModAlt | ModNoRepeat, VkSpace);
        if (!QuickPromptAvailable) Log.Info("Ctrl+Alt+Space is in use by another app, so the quick prompt shortcut is off.");
    }

    /// <summary>Claims Esc system-wide. Only claim it for a moment, so Esc keeps working normally in other apps.</summary>
    public void ListenForEscape(bool on)
    {
        if (on == _escapeRegistered) return;
        if (on) _escapeRegistered = RegisterHotKey(_source.Handle, EscapeId, ModNoRepeat, VkEscape);
        else
        {
            UnregisterHotKey(_source.Handle, EscapeId);
            _escapeRegistered = false;
        }
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case QuickPromptId: QuickPrompt?.Invoke(); break;
            case EscapeId: Escape?.Invoke(); break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_source.Handle, QuickPromptId);
        ListenForEscape(false);
        _source.RemoveHook(Hook);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
