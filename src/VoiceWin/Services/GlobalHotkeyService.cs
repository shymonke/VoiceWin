using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VoiceWin.Services;

public class GlobalHotkeyService : IDisposable
{
    private readonly nint _keyboardHookId;
    private readonly LowLevelInputProc _keyboardHookProc;
    private readonly LowLevelInputProc _mouseHookProc;
    private nint _mouseHookId;
    private bool _isKeyDown;
    private DateTime _keyDownTime;

    // Low-level hooks are global and synchronous: every keystroke and mouse event in Windows
    // waits on this callback before any app sees it. So the callback must never do real work.
    // Subscriber code (opening the audio device, network calls) is handed to this queue and
    // run on one dedicated thread, which keeps press/release strictly in order.
    private readonly BlockingCollection<bool> _eventQueue = new();
    private readonly Thread _dispatchThread;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;

    public int TargetVirtualKey { get; set; } = 165;
    public int TargetModifiers { get; set; } = 0;
    public string Mode { get; set; } = "hold";

    /// <summary>Ordered sequence of virtual-key codes. When set, replaces the
    /// TargetVirtualKey/TargetModifiers matching and requires exact press order.</summary>
    private List<int> _sequence = new();

    // Sequence matching state.
    private readonly List<int> _matched = new();      // sequence prefix currently held, in press order
    private readonly HashSet<int> _swallowed = new(); // inputs whose key-down we suppressed
    private readonly HashSet<int> _physicallyDown = new();

    // Written on the hook thread, but also cleared by CancelToggle() from the dispatch thread.
    private volatile bool _toggleState;
    private volatile bool _isRecording;
    private volatile bool _recordingStartedByTap;
    private const int HybridHoldThresholdMs = 250;

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;
    private const int VK_XBUTTON1 = 0x05; // "MB4"
    private const int VK_XBUTTON2 = 0x06; // "MB5"

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    private delegate nint LowLevelInputProc(int nCode, nint wParam, nint lParam);

    public GlobalHotkeyService()
    {
        _keyboardHookProc = KeyboardHookCallback;
        _mouseHookProc = MouseHookCallback;
        _keyboardHookId = SetHook(WH_KEYBOARD_LL, _keyboardHookProc);

        _dispatchThread = new Thread(DispatchLoop)
        {
            IsBackground = true,
            Name = "VoiceWin.HotkeyDispatch"
        };
        _dispatchThread.Start();
    }

    private void DispatchLoop()
    {
        foreach (var pressed in _eventQueue.GetConsumingEnumerable())
        {
            try
            {
                if (pressed)
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                else
                    HotkeyReleased?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // A failing subscriber must not kill the dispatch thread, or the hotkey
                // would silently stop working for the rest of the session.
            }
        }
    }

    /// <summary>Queues an event for the dispatch thread. Called from the hook callback,
    /// so it must return immediately and never throw.</summary>
    private void Raise(bool pressed)
    {
        try
        {
            if (!_eventQueue.IsAddingCompleted)
                _eventQueue.Add(pressed);
        }
        catch (InvalidOperationException)
        {
            // Queue was completed by Dispose between the check and the Add.
        }
    }

    public static bool IsMouseButton(int vk) =>
        vk is VK_RBUTTON or VK_MBUTTON or VK_XBUTTON1 or VK_XBUTTON2;

    /// <summary>Sets the ordered input sequence. Pass an empty list to fall back to
    /// TargetVirtualKey/TargetModifiers matching. Installs the mouse hook only when needed.</summary>
    public void SetSequence(IEnumerable<int>? sequence)
    {
        _sequence = (sequence ?? Enumerable.Empty<int>()).Select(Normalize).ToList();
        ResetSequenceState();

        bool needsMouse = _sequence.Any(IsMouseButton);
        if (needsMouse && _mouseHookId == 0)
            _mouseHookId = SetHook(WH_MOUSE_LL, _mouseHookProc);
        else if (!needsMouse && _mouseHookId != 0)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = 0;
        }
    }

    private static nint SetHook(int hookType, LowLevelInputProc proc)
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(hookType, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    /// <summary>Collapses left/right modifier variants so a recorded "Ctrl" matches either Ctrl key.</summary>
    private static int Normalize(int vk) => vk switch
    {
        VK_LSHIFT or VK_RSHIFT => VK_SHIFT,
        VK_LCONTROL or VK_RCONTROL => VK_CONTROL,
        VK_LMENU or VK_RMENU => VK_MENU,
        VK_RWIN => VK_LWIN,
        _ => vk
    };

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

            if (isDown || isUp)
            {
                if (_sequence.Count > 0)
                {
                    if (ProcessSequenceInput(vkCode, isDown))
                        return (nint)1;
                }
                else if (vkCode == TargetVirtualKey && AreModifiersPressed())
                {
                    HandleTrigger(isDown, isUp);
                    return (nint)1;
                }
            }
        }

        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && _sequence.Count > 0)
        {
            int message = (int)wParam;
            int vk = message switch
            {
                WM_RBUTTONDOWN or WM_RBUTTONUP => VK_RBUTTON,
                WM_MBUTTONDOWN or WM_MBUTTONUP => VK_MBUTTON,
                WM_XBUTTONDOWN or WM_XBUTTONUP => XButtonFrom(lParam),
                _ => 0
            };

            if (vk != 0)
            {
                bool isDown = message is WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;
                if (ProcessSequenceInput(vk, isDown))
                    return (nint)1;
            }
        }

        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    /// <summary>MSLLHOOKSTRUCT.mouseData sits at byte offset 8; its high word says which X button.</summary>
    private static int XButtonFrom(nint lParam)
    {
        int mouseData = Marshal.ReadInt32(lParam, 8);
        int which = (mouseData >> 16) & 0xFFFF;
        return which == 2 ? VK_XBUTTON2 : VK_XBUTTON1;
    }

    /// <summary>Feeds one key/button transition into the ordered matcher.
    /// Returns true if the event should be swallowed.</summary>
    private bool ProcessSequenceInput(int rawVk, bool isDown)
    {
        int vk = Normalize(rawVk);
        if (!_sequence.Contains(vk)) return false;

        if (isDown)
        {
            // Ignore auto-repeat: only a fresh physical press advances the sequence.
            if (!_physicallyDown.Add(vk))
                return _swallowed.Contains(vk);

            // The press only counts if it lands at the next expected position.
            if (_matched.Count < _sequence.Count && _sequence[_matched.Count] == vk)
            {
                _matched.Add(vk);
                _swallowed.Add(vk);

                if (_matched.Count == _sequence.Count)
                    HandleTrigger(isDownEvent: true, isUpEvent: false);

                return true;
            }

            // Pressed out of order - leave the event alone for the rest of the system.
            return false;
        }

        _physicallyDown.Remove(vk);
        bool suppress = _swallowed.Remove(vk);

        int index = _matched.IndexOf(vk);
        if (index >= 0)
        {
            bool wasComplete = _matched.Count == _sequence.Count;

            // Releasing any element breaks the combo; everything after it is invalidated too.
            for (int i = _matched.Count - 1; i >= index; i--)
            {
                _swallowed.Remove(_matched[i]);
                _matched.RemoveAt(i);
            }

            if (wasComplete)
                HandleTrigger(isDownEvent: false, isUpEvent: true);
        }

        return suppress;
    }

    private void ResetSequenceState()
    {
        _matched.Clear();
        _swallowed.Clear();
        _physicallyDown.Clear();
    }

    private void HandleTrigger(bool isDownEvent, bool isUpEvent)
    {
        if (Mode == "hold")
        {
            if (isDownEvent && !_isKeyDown)
            {
                _isKeyDown = true;
                _keyDownTime = DateTime.UtcNow;
                Raise(pressed: true);
            }
            else if (isUpEvent && _isKeyDown)
            {
                _isKeyDown = false;
                Raise(pressed: false);
            }
        }
        else if (Mode == "toggle")
        {
            if (isDownEvent && !_isKeyDown)
            {
                _isKeyDown = true;
                _toggleState = !_toggleState;

                Raise(pressed: _toggleState);
            }
            else if (isUpEvent)
            {
                _isKeyDown = false;
            }
        }
        else if (Mode == "hybrid")
        {
            if (isDownEvent && !_isKeyDown)
            {
                _isKeyDown = true;
                _keyDownTime = DateTime.UtcNow;

                if (!_isRecording)
                {
                    _isRecording = true;
                    _recordingStartedByTap = false;
                    Raise(pressed: true);
                }
            }
            else if (isUpEvent && _isKeyDown)
            {
                _isKeyDown = false;
                var holdDuration = (DateTime.UtcNow - _keyDownTime).TotalMilliseconds;

                if (holdDuration >= HybridHoldThresholdMs)
                {
                    // Hold gesture: stop on release.
                    if (_isRecording)
                    {
                        _isRecording = false;
                        _recordingStartedByTap = false;
                        Raise(pressed: false);
                    }
                }
                else if (!_recordingStartedByTap)
                {
                    // First quick tap: keep recording (toggle on).
                    _recordingStartedByTap = true;
                }
                else
                {
                    // Second quick tap: toggle off.
                    _isRecording = false;
                    _recordingStartedByTap = false;
                    Raise(pressed: false);
                }
            }
        }
    }

    public void CancelToggle()
    {
        _toggleState = false;
        _isRecording = false;
        _recordingStartedByTap = false;
    }

    public void Dispose()
    {
        UnhookWindowsHookEx(_keyboardHookId);
        if (_mouseHookId != 0)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = 0;
        }

        _eventQueue.CompleteAdding();
        _dispatchThread.Join(TimeSpan.FromSeconds(2));
        _eventQueue.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelInputProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private bool AreModifiersPressed()
    {
        if (TargetModifiers == 0) return true;

        bool ctrlRequired = (TargetModifiers & 1) != 0;
        bool altRequired = (TargetModifiers & 2) != 0;
        bool shiftRequired = (TargetModifiers & 4) != 0;
        bool winRequired = (TargetModifiers & 8) != 0;

        bool ctrlPressed = (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 || (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
        bool altPressed = (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0 || (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;
        bool shiftPressed = (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 || (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0;
        bool winPressed = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

        return (!ctrlRequired || ctrlPressed) &&
               (!altRequired || altPressed) &&
               (!shiftRequired || shiftPressed) &&
               (!winRequired || winPressed);
    }
}
