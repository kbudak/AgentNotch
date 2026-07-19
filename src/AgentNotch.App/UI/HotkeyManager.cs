using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using AgentNotch.App.Diagnostics;
using AgentNotch.App.Interop;
using AgentNotch.App.ViewModels;

namespace AgentNotch.App.UI;

/// <summary>
/// System-wide hotkeys for resolving the active prompt. Because the overlay is
/// WS_EX_NOACTIVATE it never receives keyboard input, so Ctrl+Y / Ctrl+N /
/// Ctrl+1..9 are registered globally — but ONLY while a prompt is pending, and
/// unregistered the instant the queue empties.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    // Stable ids (arbitrary but unique within the process).
    private const int IdAllow = 0xB100;      // Ctrl+Y
    private const int IdDenyOrPass = 0xB101; // Ctrl+N
    private const int IdDigit1 = 0xB110;     // Ctrl+1 .. Ctrl+9 → IdDigit1..IdDigit1+8

    private const uint VkY = 0x59;
    private const uint VkN = 0x4E;
    private const uint Vk1 = 0x31;

    private readonly Window _host;
    private readonly MainViewModel _vm;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private PromptViewModel? _registeredFor;
    private bool _disposed;

    public HotkeyManager(Window host, MainViewModel vm)
    {
        _host = host;
        _vm = vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        if (new WindowInteropHelper(host).Handle != IntPtr.Zero) Attach();
        else host.SourceInitialized += OnHostSourceInitialized;
    }

    private void OnHostSourceInitialized(object? sender, EventArgs e) => Attach();

    private void Attach()
    {
        try
        {
            _hwnd = new WindowInteropHelper(_host).Handle;
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
            Update();
        }
        catch (Exception ex)
        {
            AppLog.Write("hotkeys attach: " + ex.Message);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || e.PropertyName == nameof(MainViewModel.ActivePrompt))
            Update();
    }

    /// <summary>(Re)register for the current active prompt; unregister everything when idle.</summary>
    private void Update()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;

        var prompt = _vm.ActivePrompt;
        if (ReferenceEquals(prompt, _registeredFor)) return;

        UnregisterAll();
        _registeredFor = prompt;
        if (prompt is null) return;

        uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT;
        if (prompt.Kind == PromptKind.Permission)
        {
            Register(IdAllow, mods, VkY);
            Register(IdDenyOrPass, mods, VkN);
        }
        else
        {
            Register(IdDenyOrPass, mods, VkN); // pass back to terminal
            int count = Math.Min(9, prompt.Options.Count);
            for (int i = 0; i < count; i++)
                Register(IdDigit1 + i, mods, Vk1 + (uint)i);
        }
    }

    private void Register(int id, uint mods, uint vk)
    {
        try
        {
            if (!NativeMethods.RegisterHotKey(_hwnd, id, mods, vk))
                AppLog.Write($"hotkey register failed: id=0x{id:X} vk=0x{vk:X}");
        }
        catch (Exception ex)
        {
            AppLog.Write("hotkey register: " + ex.Message);
        }
    }

    private void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            NativeMethods.UnregisterHotKey(_hwnd, IdAllow);
            NativeMethods.UnregisterHotKey(_hwnd, IdDenyOrPass);
            for (int i = 0; i < 9; i++)
                NativeMethods.UnregisterHotKey(_hwnd, IdDigit1 + i);
        }
        catch (Exception ex)
        {
            AppLog.Write("hotkey unregister: " + ex.Message);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && !_disposed)
        {
            try
            {
                int id = wParam.ToInt32();
                var prompt = _vm.ActivePrompt;
                if (prompt is not null)
                {
                    if (id == IdAllow && prompt.Kind == PromptKind.Permission)
                    {
                        handled = true;
                        _vm.AllowActive();
                    }
                    else if (id == IdDenyOrPass)
                    {
                        handled = true;
                        if (prompt.Kind == PromptKind.Permission) _vm.DenyActive();
                        else _vm.PassActive();
                    }
                    else if (id >= IdDigit1 && id < IdDigit1 + 9)
                    {
                        handled = true;
                        _vm.ChooseOption(id - IdDigit1);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("hotkey dispatch: " + ex);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _vm.PropertyChanged -= OnVmPropertyChanged; } catch { }
        try { _host.SourceInitialized -= OnHostSourceInitialized; } catch { }
        try { UnregisterAll(); } catch { }
        try { _source?.RemoveHook(WndProc); } catch { }
        _registeredFor = null;
    }
}
