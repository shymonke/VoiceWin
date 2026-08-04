using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceWin.Services;

namespace VoiceWin.Views;

public partial class MainWindow : Window
{
    private readonly App _app;
    private readonly TrayIconService _trayIconService;
    private readonly StatusOverlayWindow _statusOverlay;
    private bool _isRecordingHotkey;
    private int _pendingHotkeyVirtualKey;
    private int _pendingHotkeyModifiers;
    private List<int> _pendingHotkeySequence = new();

    // While recording: every key/button pressed, in the order it went down.
    private readonly List<int> _captureOrder = new();
    private readonly HashSet<int> _captureHeld = new();

    public MainWindow()
    {
        InitializeComponent();
        _app = (App)Application.Current;
        _trayIconService = new TrayIconService();
        _statusOverlay = new StatusOverlayWindow();

        LoadSettings();
        SubscribeToEvents();

        TaskbarIcon.IconSource = _trayIconService.CreateIconWithStatus(TrayStatus.Ready);

        if (_app.SettingsService.Settings.StartMinimized)
        {
            ShowActivated = false;
            WindowState = WindowState.Minimized;
        }
    }

    private void LoadSettings()
    {
        var settings = _app.SettingsService.Settings;

        GroqApiKeyBox.Text = settings.GroqApiKey ?? "";
        DeepgramApiKeyBox.Text = settings.DeepgramApiKey ?? "";

        SelectComboItemByTag(ProviderCombo, settings.TranscriptionProvider);
        PopulateInputDevices(settings.InputDeviceNumber);
        SelectComboItemByTag(GroqModelCombo, settings.GroqModel);
        SelectComboItemByTag(DeepgramModelCombo, settings.DeepgramModel);
        SelectComboItemByTag(HotkeyModeCombo, settings.HotkeyMode);
        SelectComboItemByTag(OverlayPositionCombo, settings.OverlayPosition);
        SelectComboItemByTag(LanguageCombo, settings.Language);

        AiEnhancementCheckBox.IsChecked = settings.AiEnhancementEnabled;
        SelectComboItemByTag(AiEnhancementModelCombo, settings.AiEnhancementModel);
        AiEnhancementPromptBox.Text = settings.AiEnhancementPrompt;

        VadSilenceTimeoutBox.Text = settings.VadStreamingSilenceTimeoutSeconds.ToString();

        ShowOverlayCheckBox.IsChecked = settings.ShowRecordingOverlay;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        StartMinimizedCheckBox.IsChecked = settings.StartMinimized;

        _pendingHotkeyVirtualKey = settings.HotkeyVirtualKey;
        _pendingHotkeyModifiers = settings.HotkeyModifiers;
        _pendingHotkeySequence = new List<int>(settings.HotkeySequence);
        UpdateHotkeyDisplay();

        _statusOverlay.SetPosition(settings.OverlayPosition);
    }

    private void PopulateInputDevices(int selectedDeviceNumber)
    {
        InputDeviceCombo.Items.Clear();
        InputDeviceCombo.Items.Add(new ComboBoxItem { Content = "System Default", Tag = "-1" });

        foreach (var device in AudioRecordingService.GetInputDevices())
        {
            InputDeviceCombo.Items.Add(new ComboBoxItem
            {
                Content = device.Name,
                Tag = device.Number.ToString()
            });
        }

        SelectComboItemByTag(InputDeviceCombo, selectedDeviceNumber.ToString());
    }

    private void SelectComboItemByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private string GetSelectedTag(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    }

    private void SubscribeToEvents()
    {
        _app.Orchestrator.RecordingStarted += (s, e) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = "Recording...";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                TaskbarIcon.ToolTipText = "VoiceWin - Recording...";
                TaskbarIcon.IconSource = _trayIconService.CreateIconWithStatus(TrayStatus.Recording);
                
                if (_app.SettingsService.Settings.ShowRecordingOverlay)
                {
                    bool isStreaming = _app.SettingsService.Settings.TranscriptionProvider == "deepgram-streaming";
                    _statusOverlay.SetMode(isStreaming);
                    _statusOverlay.UpdateStatus("Recording");
                    _statusOverlay.StartAnimating();
                }
            });
        };

        _app.Orchestrator.RecordingStopped += (s, e) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = "Processing...";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(234, 179, 8));
                TaskbarIcon.IconSource = _trayIconService.CreateIconWithStatus(TrayStatus.Processing);
                
                _statusOverlay.StopAnimating();
                _statusOverlay.UpdateStatus("Processing");
            });
        };

        _app.Orchestrator.StatusChanged += (s, status) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = status;
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                TaskbarIcon.ToolTipText = $"VoiceWin - {status}";
                TaskbarIcon.IconSource = _trayIconService.CreateIconWithStatus(TrayStatus.Ready);
                
                _statusOverlay.UpdateStatus(status);
                if (IsTerminalStatus(status))
                {
                    _statusOverlay.HideOverlay();
                }
            });
        };

        _app.Orchestrator.TranscriptionCompleted += (s, result) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (result.Success)
                {
                    StatusText.Text = $"Done ({result.Duration.TotalMilliseconds:F0}ms)";
                }
            });
        };

        _app.Orchestrator.AudioLevelChanged += (s, level) =>
        {
            _statusOverlay.UpdateAudioLevel(level);
        };

        _app.Orchestrator.SpeechDetected += (s, isSpeaking) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _statusOverlay.UpdateStatus(isSpeaking ? "Recording" : "Listening");
            });
        };
    }

    private static bool IsTerminalStatus(string status)
    {
        return status.Contains("Transcribed") || 
               status.Contains("Streamed") || 
               status.Contains("Error") ||
               status.Contains("too short") ||
               status.Contains("No API") ||
               status.Contains("No valid") ||
               status.Contains("No speech") ||
               status.Contains("Auto-stopped");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(VadSilenceTimeoutBox.Text, out int timeout) || timeout < 0)
        {
            StatusText.Text = "Invalid silence timeout (must be a non-negative number)";
            return;
        }

        _app.SettingsService.UpdateSettings(settings =>
        {
            settings.GroqApiKey = GroqApiKeyBox.Text.Trim();
            settings.DeepgramApiKey = DeepgramApiKeyBox.Text.Trim();
            settings.TranscriptionProvider = GetSelectedTag(ProviderCombo);
            if (int.TryParse(GetSelectedTag(InputDeviceCombo), out int deviceNumber))
            {
                settings.InputDeviceNumber = deviceNumber;
            }
            settings.GroqModel = GetSelectedTag(GroqModelCombo);
            settings.DeepgramModel = GetSelectedTag(DeepgramModelCombo);
            settings.HotkeyMode = GetSelectedTag(HotkeyModeCombo);
            settings.OverlayPosition = GetSelectedTag(OverlayPositionCombo);
            settings.Language = GetSelectedTag(LanguageCombo);
            settings.AiEnhancementEnabled = AiEnhancementCheckBox.IsChecked ?? false;
            settings.AiEnhancementModel = GetSelectedTag(AiEnhancementModelCombo);
            settings.AiEnhancementPrompt = AiEnhancementPromptBox.Text;
            settings.VadStreamingSilenceTimeoutSeconds = timeout;
            settings.ShowRecordingOverlay = ShowOverlayCheckBox.IsChecked ?? true;
            settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked ?? false;
            settings.StartMinimized = StartMinimizedCheckBox.IsChecked ?? true;
            settings.HotkeyVirtualKey = _pendingHotkeyVirtualKey;
            settings.HotkeyModifiers = _pendingHotkeyModifiers;
            settings.HotkeySequence = new List<int>(_pendingHotkeySequence);
        });

        _app.Orchestrator.UpdateHotkeySettings();
        _statusOverlay.SetPosition(_app.SettingsService.Settings.OverlayPosition);
        ApplyStartWithWindows(_app.SettingsService.Settings.StartWithWindows);

        StatusText.Text = "Settings saved!";
        ResetStatusTextAfterDelay();
    }

    private void ResetStatusTextAfterDelay()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            StatusText.Text = "Ready - Press your hotkey to record";
        };
        timer.Start();
    }

    private static void ApplyStartWithWindows(bool enabled)
    {
        const string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "VoiceWin";

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true);
        if (key == null) return;

        if (enabled)
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(valueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private void ShowWindow_Click(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void TaskbarIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void RecordHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            StopRecordingHotkey();
            return;
        }

        _isRecordingHotkey = true;
        _captureOrder.Clear();
        _captureHeld.Clear();
        RecordHotkeyButton.Content = "Cancel";
        HotkeyDisplayBox.Text = "Press keys / mouse buttons in order...";
        HotkeyDisplayBox.Focus();
    }

    private void StopRecordingHotkey()
    {
        _isRecordingHotkey = false;
        _captureOrder.Clear();
        _captureHeld.Clear();
        RecordHotkeyButton.Content = "Record";
        UpdateHotkeyDisplay();
    }

    private void UpdateHotkeyDisplay()
    {
        HotkeyDisplayBox.Text = _pendingHotkeySequence.Count > 0
            ? string.Join(" → ", _pendingHotkeySequence.Select(GetKeyName))
            : GetHotkeyDisplayString(_pendingHotkeyModifiers, _pendingHotkeyVirtualKey);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (!_isRecordingHotkey) return;

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vkCode = KeyInterop.VirtualKeyFromKey(key);
        if (vkCode == 0) return;

        CaptureInputDown(vkCode);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        if (!_isRecordingHotkey) return;

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vkCode = KeyInterop.VirtualKeyFromKey(key);
        if (vkCode == 0) return;

        CaptureInputUp(vkCode);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (!_isRecordingHotkey) return;

        int vkCode = MouseButtonToVirtualKey(e.ChangedButton);
        if (vkCode == 0) return; // left click stays usable so the Cancel button still works

        e.Handled = true;
        CaptureInputDown(vkCode);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);

        if (!_isRecordingHotkey) return;

        int vkCode = MouseButtonToVirtualKey(e.ChangedButton);
        if (vkCode == 0) return;

        e.Handled = true;
        CaptureInputUp(vkCode);
    }

    private static int MouseButtonToVirtualKey(MouseButton button) => button switch
    {
        MouseButton.Right => 0x02,
        MouseButton.Middle => 0x04,
        MouseButton.XButton1 => 0x05,
        MouseButton.XButton2 => 0x06,
        _ => 0 // Left is deliberately not bindable
    };

    private void CaptureInputDown(int vkCode)
    {
        if (!_captureHeld.Add(vkCode)) return; // ignore auto-repeat

        _captureOrder.Add(vkCode);
        HotkeyDisplayBox.Text = string.Join(" → ", _captureOrder.Select(GetKeyName)) + " ...";
    }

    private void CaptureInputUp(int vkCode)
    {
        _captureHeld.Remove(vkCode);

        // Finalise once everything the user pressed has been let go.
        if (_captureHeld.Count > 0 || _captureOrder.Count == 0) return;

        var order = new List<int>(_captureOrder);
        _captureOrder.Clear();

        if (order.Any(IsMouseVirtualKey))
        {
            // Mouse combos are stored as an ordered sequence and must be pressed in this order.
            _pendingHotkeySequence = order;
        }
        else
        {
            // Keyboard-only combos keep the existing order-independent modifier+key form.
            _pendingHotkeySequence = new List<int>();
            var nonModifiers = order.Where(v => !IsModifierKey(v)).ToList();

            int modifiers = 0;
            foreach (var vk in order) modifiers |= ModifierMask(vk);

            if (nonModifiers.Count > 0)
            {
                _pendingHotkeyVirtualKey = nonModifiers[^1];
            }
            else
            {
                // All modifiers, e.g. plain Right Alt: the last one pressed is the trigger.
                _pendingHotkeyVirtualKey = order[^1];
                modifiers &= ~ModifierMask(order[^1]);
            }

            _pendingHotkeyModifiers = modifiers;
        }

        _isRecordingHotkey = false;
        RecordHotkeyButton.Content = "Record";
        UpdateHotkeyDisplay();
    }

    private static bool IsMouseVirtualKey(int vkCode) => vkCode is 0x02 or 0x04 or 0x05 or 0x06;

    private static bool IsModifierKey(int vkCode)
    {
        return vkCode is 160 or 161 or 162 or 163 or 164 or 165 or 91 or 92 or 16 or 17 or 18;
    }

    private static int ModifierMask(int vkCode) => vkCode switch
    {
        162 or 163 or 17 => 1, // Ctrl
        164 or 165 or 18 => 2, // Alt
        160 or 161 or 16 => 4, // Shift
        91 or 92 => 8,         // Win
        _ => 0
    };

    private static string GetModifierString(int modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & 1) != 0) parts.Add("Ctrl");
        if ((modifiers & 2) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        return parts.Count > 0 ? string.Join(" + ", parts) : "";
    }

    private static string GetHotkeyDisplayString(int modifiers, int virtualKey)
    {
        var modStr = GetModifierString(modifiers);
        var keyStr = GetKeyName(virtualKey);
        return string.IsNullOrEmpty(modStr) ? keyStr : $"{modStr} + {keyStr}";
    }

    private static string GetKeyName(int virtualKey)
    {
        return virtualKey switch
        {
            0x02 => "Right Click",
            0x04 => "Middle Click",
            0x05 => "MB4",
            0x06 => "MB5",
            8 => "Backspace",
            9 => "Tab",
            13 => "Enter",
            16 => "Shift",
            17 => "Ctrl",
            18 => "Alt",
            19 => "Pause",
            20 => "Caps Lock",
            27 => "Escape",
            32 => "Space",
            33 => "Page Up",
            34 => "Page Down",
            35 => "End",
            36 => "Home",
            37 => "Left Arrow",
            38 => "Up Arrow",
            39 => "Right Arrow",
            40 => "Down Arrow",
            45 => "Insert",
            46 => "Delete",
            91 => "Left Win",
            92 => "Right Win",
            93 => "Menu",
            112 => "F1",
            113 => "F2",
            114 => "F3",
            115 => "F4",
            116 => "F5",
            117 => "F6",
            118 => "F7",
            119 => "F8",
            120 => "F9",
            121 => "F10",
            122 => "F11",
            123 => "F12",
            144 => "Num Lock",
            145 => "Scroll Lock",
            160 => "Left Shift",
            161 => "Right Shift",
            162 => "Left Ctrl",
            163 => "Right Ctrl",
            164 => "Left Alt",
            165 => "Right Alt",
            186 => ";",
            187 => "=",
            188 => ",",
            189 => "-",
            190 => ".",
            191 => "/",
            192 => "`",
            219 => "[",
            220 => "\\",
            221 => "]",
            222 => "'",
            _ when virtualKey >= 48 && virtualKey <= 57 => ((char)virtualKey).ToString(),
            _ when virtualKey >= 65 && virtualKey <= 90 => ((char)virtualKey).ToString(),
            _ when virtualKey >= 96 && virtualKey <= 105 => $"Numpad {virtualKey - 96}",
            _ => $"Key {virtualKey}"
        };
    }
}
