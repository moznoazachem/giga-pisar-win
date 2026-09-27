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
    private readonly Func<BrainSource, Task> _selectBrain;
    private bool _loading = true;

    public SettingsWindow(Settings settings, Action apply, Action unpin, Func<BrainSource, Task> selectBrain)
    {
        _settings = settings;
        _apply = apply;
        _unpin = unpin;
        _selectBrain = selectBrain;
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

        CleanupHeading.Text = L.T("Мозг", "Brain");
        CleanupHint.Text = L.T("Нейросеть правит надиктованное по команде. Скажите в конце фразы: «Писарь, исправь», «Писарь, сократи», «Писарь, сделай вежливее», «Писарь, переведи на английский». Без обращения текст вставляется сразу, как обычно.",
                               "An AI model edits the dictated text on command. End a phrase with \"Pisar, fix it\", \"Pisar, make it shorter\" or \"Pisar, translate into English\" (said in Russian). Without the address the text is inserted at once, as usual.");
        string host = SpeechCleanup.HostOf(_settings.CleanupEndpointUrl);
        BrainBox.Items.Clear();
        BrainBox.Items.Add(new ComboBoxItem { Content = L.T("Выключен", "Off"), Tag = BrainSource.Off });
        BrainBox.Items.Add(new ComboBoxItem
        {
            Content = LocalBrain.Downloaded ? L.T($"На компьютере ({LocalBrain.ModelTitle})", $"On this computer ({LocalBrain.ModelTitle})")
                                            : L.T("На компьютере (скачать 2 ГБ)", "On this computer (download 2 GB)"),
            Tag = BrainSource.Local,
        });
        BrainBox.Items.Add(new ComboBoxItem
        {
            Content = host.Length > 0 ? L.T($"Свой сервер или облако ({host})", $"Own server or cloud ({host})")
                                      : L.T("Свой сервер или облако", "Own server or cloud"),
            Tag = BrainSource.Server,
        });
        BrainBox.SelectedIndex = (int)_settings.Brain;
        BrainStatus.Text = _settings.Brain switch
        {
            BrainSource.Local => L.T("Работает без интернета. Занимает около 2,5 ГБ памяти, пока нужен, и выгружается через 15 минут без дела.",
                                     "Works offline. Takes about 2.5 GB of memory while needed and unloads after 15 idle minutes."),
            BrainSource.Server => L.T($"Текст (не звук) уходит на {host}. Распознавание остаётся на компьютере.",
                                      $"Text (not audio) goes to {host}. Recognition stays on this computer."),
            _ => L.T("Текст вставляется как распознан.", "Text is inserted as recognized."),
        };
        CleanupButton.Content = L.T("Настроить сервер…", "Set up the server…");
        CleanupButton.Visibility = _settings.Brain == BrainSource.Server ? Visibility.Visible : Visibility.Collapsed;
        DeleteBrainButton.Content = L.T("Удалить модель с компьютера", "Delete the model from this computer");
        DeleteBrainButton.Visibility = LocalBrain.Downloaded && _settings.Brain != BrainSource.Local ? Visibility.Visible : Visibility.Collapsed;
        EveryTakeBox.Content = L.T("Править каждую диктовку, без команды", "Edit every take, without a command");
        EveryTakeBox.IsChecked = _settings.BrainEveryTake;
        EveryTakeBox.IsEnabled = _settings.Brain != BrainSource.Off;
        EveryTakeHint.Text = L.T("Удобно с быстрым сервером. На компьютере без видеокарты каждая вставка будет ждать нейросеть несколько секунд.",
                                 "Handy with a fast server. On a computer without a graphics card every insertion will wait a few seconds for the model.");

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

    private async void Brain_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BrainBox.SelectedItem is not ComboBoxItem item) return;
        var source = (BrainSource)item.Tag;
        if (source == _settings.Brain) return;
        await _selectBrain(source);
        Localize();   // the choice may have been cancelled (download declined, server not set up)
    }

    private void EveryTake_Click(object sender, RoutedEventArgs e)
    {
        _settings.BrainEveryTake = EveryTakeBox.IsChecked == true;
        _apply();
    }

    private void DeleteBrain_Click(object sender, RoutedEventArgs e)
    {
        LocalBrain.DeleteModel();
        Localize();
    }

    private void Cleanup_Click(object sender, RoutedEventArgs e) => OpenServerSettings();

    /// <summary>Server address, key and model; saving them switches the Brain to the server.</summary>
    public void OpenServerSettings()
    {
        new CleanupSettingsWindow(_settings, _apply) { Owner = this }.ShowDialog();
        Localize();
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
