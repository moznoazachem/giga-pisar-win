// Settings window. Every change is applied and saved immediately.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace GigaPisar.App;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Action _apply;
    private readonly Action _unpin;
    private bool _loading = true;

    public SettingsWindow(Settings settings, Action apply, Action unpin)
    {
        _settings = settings;
        _apply = apply;
        _unpin = unpin;
        InitializeComponent();
        Localize();
    }

    /// <summary>Fills every text in the current UI language; called again when the language changes.</summary>
    public void Localize()
    {
        _loading = true;
        Title = L.T("Гига Писарь: настройки", "Giga Pisar: settings");
        Heading.Text = L.T("Диктовка", "Dictation");
        Intro.Text = L.T("Поставьте курсор в любой текст, зажмите клавишу и говорите. Отпустите: текст появится сам.",
                         "Put the cursor in any text, hold the key and speak. Release: the text appears by itself.");
        HotkeyLabel.Text = L.T("Клавиша диктовки", "Dictation key");
        InsertLabel.Text = L.T("Как вставлять текст", "How to insert text");
        LanguageLabel.Text = L.T("Язык интерфейса", "Interface language");

        HotkeyBox.Items.Clear();
        foreach (var (vk, ru, en) in Settings.HotkeyChoices)
            HotkeyBox.Items.Add(new ComboBoxItem { Content = L.T(ru, en), Tag = vk });
        HotkeyBox.SelectedIndex = Math.Max(0, Array.FindIndex(Settings.HotkeyChoices, c => c.vk == _settings.HotkeyVk));

        InsertBox.Items.Clear();
        InsertBox.Items.Add(new ComboBoxItem { Content = L.T("Печатать как с клавиатуры", "Type like a keyboard") });
        InsertBox.Items.Add(new ComboBoxItem { Content = L.T("Через буфер обмена (Ctrl+V)", "Through the clipboard (Ctrl+V)") });
        InsertBox.SelectedIndex = _settings.InsertMode == InsertMode.Paste ? 1 : 0;

        LanguageBox.Items.Clear();
        LanguageBox.Items.Add(new ComboBoxItem { Content = L.T("Как в системе", "Same as system"), Tag = UiLanguage.Auto });
        LanguageBox.Items.Add(new ComboBoxItem { Content = "Русский", Tag = UiLanguage.Russian });
        LanguageBox.Items.Add(new ComboBoxItem { Content = "English", Tag = UiLanguage.English });
        LanguageBox.SelectedIndex = (int)_settings.Language;

        OverlayBox.Content = L.T("Показывать плашку с волной во время записи", "Show the wave panel while recording");
        AutostartBox.Content = L.T("Запускать при входе в Windows", "Start when I sign in to Windows");
        KeepBox.Content = L.T("Сохранять последнюю запись для разбора ошибок", "Keep the last recording for troubleshooting");
        OverlayBox.IsChecked = _settings.ShowOverlay;
        OverlayHint.Text = L.T("Плашку можно перетащить мышью, пока она видна: она запомнит место.",
                               "Drag the pill with the mouse while it is visible and it will stay there.");
        UnpinButton.Content = L.T("Вернуть плашку к курсору", "Put the pill back at the caret");
        UnpinButton.IsEnabled = _settings.OverlayX != null;
        AutostartBox.IsChecked = Autostart.IsEnabled();
        KeepBox.IsChecked = _settings.KeepLastRecording;
        UpdatesBox.Content = L.T("Проверять обновления и предлагать их", "Check for updates and offer them");
        UpdatesBox.IsChecked = _settings.CheckUpdates;

        AboutHeading.Text = L.T("О программе", "About");
        About.Text = L.T($"Версия {PisarApp.Version}. Распознавание идёт на вашем компьютере, звук никуда не отправляется. Модель лежит в {Settings.ModelDir}.",
                         $"Version {PisarApp.Version}. Recognition runs on your computer; audio never leaves it. The model lives in {Settings.ModelDir}.");
        MicLine.Text = L.T("Микрофон: ", "Microphone: ") + Recorder.DefaultDeviceName();
        ModelLink.Text = L.T("модель GigaAM от Сбера", "GigaAM model by Sber");
        LogLink.Text = L.T("Открыть папку с журналом", "Open the log folder");
        _loading = false;
    }

    private void Hotkey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HotkeyBox.SelectedItem is not ComboBoxItem item) return;
        _settings.HotkeyVk = (int)item.Tag;
        _apply();
    }

    private void Insert_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.InsertMode = InsertBox.SelectedIndex == 1 ? InsertMode.Paste : InsertMode.Type;
        _apply();
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not ComboBoxItem item) return;
        _settings.Language = (UiLanguage)item.Tag;
        _apply();
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowOverlay = OverlayBox.IsChecked == true;
        _apply();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e) => Autostart.Set(AutostartBox.IsChecked == true);

    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        _settings.CheckUpdates = UpdatesBox.IsChecked == true;
        _apply();
    }

    private void Unpin_Click(object sender, RoutedEventArgs e)
    {
        _unpin();
        UnpinButton.IsEnabled = false;
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        _settings.KeepLastRecording = KeepBox.IsChecked == true;
        if (!_settings.KeepLastRecording)
            try { File.Delete(Settings.LastTakePath); } catch { }
        _apply();
    }

    private void Link_Click(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.LocalDataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", Settings.LocalDataDir) { UseShellExecute = true });
        }
        catch { }
    }
}
