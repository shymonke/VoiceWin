namespace VoiceWin.Models;

public class AppSettings
{
    public string? GroqApiKey { get; set; }
    public string? DeepgramApiKey { get; set; }
    public string TranscriptionProvider { get; set; } = "groq";
    public string GroqModel { get; set; } = "whisper-large-v3-turbo";
    public int InputDeviceNumber { get; set; } = -1; // -1 = system default
    public string DeepgramModel { get; set; } = "nova-3";
    public string HotkeyMode { get; set; } = "hold";
    public int HotkeyVirtualKey { get; set; } = 165;
    public int HotkeyModifiers { get; set; } = 0; // 0=None, 1=Ctrl, 2=Alt, 4=Shift, 8=Win (combinable)

    // Ordered input sequence, used instead of HotkeyVirtualKey/HotkeyModifiers when non-empty.
    // Only populated for combos containing a mouse button (VK 0x02/0x04/0x05/0x06), which must
    // be pressed in exactly this order. Keyboard-only combos stay order-independent.
    public List<int> HotkeySequence { get; set; } = new();
    public string Language { get; set; } = "multi";
    public bool PlaySoundFeedback { get; set; } = true;
    public bool ShowRecordingOverlay { get; set; } = true;
    public string OverlayPosition { get; set; } = "bottom";
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = true;
    
    // AI Enhancement
    public bool AiEnhancementEnabled { get; set; } = false;
    public string AiEnhancementPrompt { get; set; } = @"Clean up the <TRANSCRIPT> text with minimal changes:
- Keep the exact words and phrasing - do not rephrase or rewrite
- Remove filler words (um, uh, like, you know, so, basically)
- Remove stutters and false starts
- Collapse repetitions (e.g., 'I I I think' → 'I think')
- Fix obvious transcription errors only
- Do not change sentence structure
- Do not improve word choice
- Use all lowercase letters, no capitalization at all
- Minimize punctuation, use commas sparingly, avoid periods unless absolutely necessary
- Keep it casual
- IMPORTANT: Always add a single trailing space at the end of your output
- Output only the cleaned text, nothing else";
    public string AiEnhancementModel { get; set; } = "moonshotai/kimi-k2-instruct-0905";

    // Streaming auto-stop (server-side silence detection)
    public int VadStreamingSilenceTimeoutSeconds { get; set; } = 15;
}
