using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VoiceWin;

public partial class App : Application
{
    private Services.TranscriptionOrchestrator? _orchestrator;
    private Services.SettingsService? _settingsService;

    public Services.SettingsService SettingsService => _settingsService!;
    public Services.TranscriptionOrchestrator Orchestrator => _orchestrator!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settingsService = new Services.SettingsService();
        _orchestrator = new Services.TranscriptionOrchestrator(_settingsService);
    }

    /// <summary>Stops a closed combo box from changing its selection when you scroll the page.
    /// The wheel event is swallowed here and re-sent to the parent so the page scrolls instead.
    /// When the drop-down is open the wheel is left alone, so you can still scroll a long list.</summary>
    private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen)
            return;

        e.Handled = true;

        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = combo
        };
        (combo.Parent as UIElement)?.RaiseEvent(forwarded);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _orchestrator?.Dispose();
        base.OnExit(e);
    }
}
