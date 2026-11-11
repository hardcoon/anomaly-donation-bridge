using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.App.Services;

internal sealed class GlobalHotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ToggleProcessingId = 0x0ADB1;
    private const int RepeatDonationId = 0x0ADB2;
    private const int ActivePresetIdBase = 0x0AE00;
    private const uint ModNoRepeat = 0x4000;

    private readonly Window _window;
    private readonly BridgeRuntime _runtime;
    private readonly Func<Task> _toggleProcessing;
    private readonly Func<Task> _repeatDonation;
    private readonly Func<int, Task> _activatePreset;
    private readonly Action<string> _report;
    private readonly object _queueLock = new();
    private HwndSource? _source;
    private nint _handle;
    private Task _dispatchTail = Task.CompletedTask;
    private readonly Dictionary<int, int> _presetNumbersByHotkeyId = [];
    private bool _disposed;

    public GlobalHotkeyManager(
        Window window,
        BridgeRuntime runtime,
        Func<Task> toggleProcessing,
        Func<Task> repeatDonation,
        Func<int, Task> activatePreset,
        Action<string> report)
    {
        _window = window;
        _runtime = runtime;
        _toggleProcessing = toggleProcessing;
        _repeatDonation = repeatDonation;
        _activatePreset = activatePreset;
        _report = report;

        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("Не удалось подключить обработчик глобальных горячих клавиш.");
        _source.AddHook(WindowMessageHook);
        _runtime.SettingsChanged += SettingsChanged;
        ApplySettings();
    }

    private void SettingsChanged()
    {
        if (_disposed) return;
        if (_window.Dispatcher.CheckAccess()) ApplySettings();
        else _window.Dispatcher.BeginInvoke(ApplySettings);
    }

    private void ApplySettings()
    {
        if (_disposed) return;
        UnregisterHotKey(_handle, ToggleProcessingId);
        UnregisterHotKey(_handle, RepeatDonationId);
        foreach (var id in _presetNumbersByHotkeyId.Keys) UnregisterHotKey(_handle, id);
        _presetNumbersByHotkeyId.Clear();

        Register(
            ToggleProcessingId,
            _runtime.Settings.ToggleDonationProcessingHotkey,
            "включение и отключение обработки донатов");
        Register(
            RepeatDonationId,
            _runtime.Settings.RepeatLastDonationHotkey,
            "повтор последнего доната");
        for (var index = 0; index < _runtime.Settings.ActivePresetHotkeys.Count; index++)
        {
            var binding = _runtime.Settings.ActivePresetHotkeys[index];
            if (binding.PresetNumber < 1 || binding.PresetNumber > _runtime.Settings.Actions.Count)
            {
                Report($"Горячая клавиша для шаблона №{binding.PresetNumber} не зарегистрирована: такого активного шаблона сейчас нет.");
                continue;
            }
            var id = ActivePresetIdBase + index;
            _presetNumbersByHotkeyId[id] = binding.PresetNumber;
            Register(id, binding.Hotkey, $"активация шаблона №{binding.PresetNumber}");
        }
    }

    private void Register(int id, string value, string actionName)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!GlobalHotkeyGesture.TryParse(value, out var gesture, out var error))
        {
            Report($"Горячая клавиша для действия «{actionName}» не зарегистрирована: {error}");
            return;
        }

        if (!RegisterHotKey(_handle, id, gesture.NativeModifiers | ModNoRepeat, gesture.VirtualKey))
        {
            var detail = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            Report($"Сочетание {gesture.DisplayText} для действия «{actionName}» занято Windows или другой программой: {detail}");
        }
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey) return 0;
        var id = wParam.ToInt32();
        if (id == ToggleProcessingId)
        {
            handled = true;
            Enqueue(_toggleProcessing);
        }
        else if (id == RepeatDonationId)
        {
            handled = true;
            Enqueue(_repeatDonation);
        }
        else if (_presetNumbersByHotkeyId.TryGetValue(id, out var presetNumber))
        {
            handled = true;
            Enqueue(() => _activatePreset(presetNumber));
        }
        return 0;
    }

    private void Enqueue(Func<Task> action)
    {
        lock (_queueLock)
        {
            _dispatchTail = _dispatchTail.ContinueWith(
                async _ =>
                {
                    try { await action().ConfigureAwait(false); }
                    catch (Exception exception) { Report(exception.Message); }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private void Report(string message)
    {
        if (_disposed) return;
        if (_window.Dispatcher.CheckAccess()) _report(message);
        else _window.Dispatcher.BeginInvoke(() => _report(message));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.SettingsChanged -= SettingsChanged;
        UnregisterHotKey(_handle, ToggleProcessingId);
        UnregisterHotKey(_handle, RepeatDonationId);
        foreach (var id in _presetNumbersByHotkeyId.Keys) UnregisterHotKey(_handle, id);
        _presetNumbersByHotkeyId.Clear();
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);
}

internal readonly record struct GlobalHotkeyGesture(
    ModifierKeys Modifiers,
    Key Key,
    uint NativeModifiers,
    uint VirtualKey,
    string DisplayText)
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;

    public static string Format(Key key, ModifierKeys modifiers)
    {
        if (IsModifierKey(key)) return string.Empty;
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join('+', parts);
    }

    public static bool TryNormalize(string? value, out string normalized, out string error)
    {
        if (TryParse(value, out var gesture, out error))
        {
            normalized = gesture.DisplayText;
            return true;
        }
        normalized = string.Empty;
        return false;
    }

    public static bool TryParse(string? value, out GlobalHotkeyGesture gesture, out string error)
    {
        gesture = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "сочетание не задано";
            return false;
        }

        var tokens = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ModifierKeys modifiers = ModifierKeys.None;
        Key primary = Key.None;
        foreach (var token in tokens)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Control;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Alt;
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Shift;
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Windows;
            }
            else if (primary == Key.None && Enum.TryParse(token, true, out Key parsed) && !IsModifierKey(parsed))
            {
                primary = parsed;
            }
            else
            {
                error = $"неизвестная или лишняя клавиша «{token}»";
                return false;
            }
        }

        if (primary == Key.None)
        {
            error = "нажмите основную клавишу, а не только модификаторы";
            return false;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(primary);
        if (virtualKey <= 0)
        {
            error = "Windows не поддерживает эту клавишу как глобальную";
            return false;
        }

        uint nativeModifiers = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) nativeModifiers |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) nativeModifiers |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) nativeModifiers |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) nativeModifiers |= ModWindows;
        gesture = new GlobalHotkeyGesture(
            modifiers,
            primary,
            nativeModifiers,
            (uint)virtualKey,
            Format(primary, modifiers));
        return true;
    }

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}
