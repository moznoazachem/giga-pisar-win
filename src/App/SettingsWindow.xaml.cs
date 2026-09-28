// Settings window, laid out like Windows 11 Settings: sections on the left
// (Dictation, Brain, About), the chosen section on the right. Every change is
// applied and saved immediately, the server Brain included.

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
    /// <summary>Last open section, kept while Pisar runs.</summary>
    private static int _lastPage;

    public enum Page { Dictation, Brain, Edit, About }

    public SettingsWindow(Settings settings, Action apply, Action unpin, Func<BrainSource, Task> selectBrain)
    {
        _settings = settings;
        _apply = apply;
        _unpin = unpin;
        _selectBrain = selectBrain;
        InitializeComponent();
        ServerPanel.Saved += UpdateBrainTexts;   // the panel has already applied and saved
        Localize();
        Nav.SelectedIndex = _lastPage;
    }

    public void ShowPage(Page page) => Nav.SelectedIndex = (int)page;

    private void Nav_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex < 0) { Nav.SelectedIndex = _lastPage; return; }
        _lastPage = Nav.SelectedIndex;
        DictationPage.Visibility = _lastPage == 0 ? Visibility.Visible : Visibility.Collapsed;
        BrainPage.Visibility = _lastPage == 1 ? Visibility.Visible : Visibility.Collapsed;
        EditPage.Visibility = _lastPage == 2 ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = _lastPage == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (_lastPage == 1 && _settings.Brain == BrainSource.Server) ServerPanel.FocusKey();
    }

    /// <summary>Fills every text in the current UI language; called again when the language changes.</summary>
    public void Localize()
    {
        _loading = true;
        Title = L.T("Гига Писарь: настройки", "Giga Pisar: settings");
        NavDictation.Text = L.T("Диктовка", "Dictation");
        NavBrain.Text = L.T("Мозг", "Brain");
        NavEdit.Text = L.T("Правка выделенного", "Edit selection");
        NavAbout.Text = L.T("О программе", "About");
        Heading.Text = L.T("Диктовка", "Dictation");
        Intro.Text = L.T("Курсор в любой текст, зажмите клавишу и говорите. Отпустите, и текст появится сам.",
                         "Cursor in any text, hold the key and speak. Release and the text appears by itself.");
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
        OverlayBox.ToolTip = L.T("Плашку можно перетащить мышью, пока она видна: она запомнит место.",
                                 "Drag the pill with the mouse while it is visible and it will stay there.");
        UnpinButton.Content = L.T("Вернуть к курсору", "Back to the caret");
        UnpinButton.ToolTip = L.T("Плашка снова будет появляться у курсора", "The pill follows the caret again");
        UnpinButton.IsEnabled = _settings.OverlayX != null;
        AutostartBox.IsChecked = Autostart.IsEnabled();
        KeepBox.IsChecked = _settings.KeepLastRecording;
        UpdatesBox.Content = L.T("Проверять обновления и предлагать их", "Check for updates and offer them");
        UpdatesBox.IsChecked = _settings.CheckUpdates;

        CleanupHeading.Text = L.T("Мозг", "Brain");
        CleanupHint.Text = L.T("Нейросеть правит надиктованное по команде. Скажите в конце: «Писарь, исправь», «Писарь, сократи» или «Писарь, переведи на английский». Без обращения текст вставляется сразу.",
                               "An AI model edits the dictation on command. End with \"Pisar, fix it\", \"Pisar, make it shorter\" or \"Pisar, translate into English\" (said in Russian). Without the address the text goes in at once.");
        BrainLabel.Text = L.T("Где думает", "Runs on");
        BrainBox.Items.Clear();
        BrainBox.Items.Add(new ComboBoxItem { Content = L.T("Выключен", "Off"), Tag = BrainSource.Off });
        if (LocalBrain.Offered || _settings.Brain == BrainSource.Local)
            BrainBox.Items.Add(new ComboBoxItem
            {
                Content = LocalBrain.Downloaded ? L.T($"На компьютере ({LocalBrain.ModelTitle})", $"On this computer ({LocalBrain.ModelTitle})")
                                                : L.T("На компьютере (скачать 2 ГБ)", "On this computer (download 2 GB)"),
                Tag = BrainSource.Local,
            });
        BrainBox.Items.Add(new ComboBoxItem { Content = L.T("В облаке", "In the cloud"), Tag = BrainSource.Server });
        foreach (ComboBoxItem it in BrainBox.Items)
            if ((BrainSource)it.Tag == _settings.Brain) BrainBox.SelectedItem = it;
        ServerPanel.Bind(_settings, _apply);
        DeleteBrainButton.Content = L.T("Удалить модель с компьютера (2 ГБ)", "Delete the model from this computer (2 GB)");
        EditHeading.Text = L.T("Правка выделенного", "Edit selection");
        EditIntro.Text = L.T("Выделите текст в любой программе, зажмите клавишу диктовки и скажите, что с ним сделать. Результат встанет на место выделенного, а Ctrl+Z вернёт как было.",
                             "Select text in any app, hold the dictation key and say what to do with it. The result replaces the selection; Ctrl+Z brings the original back.");
        SelectionBox.Content = L.T("Править выделенный текст голосом", "Edit selected text by voice");
        SelectionBox.IsChecked = _settings.BrainOnSelection;
        SelectionHint.Text = L.T("Выключите, если хотите просто надиктовывать поверх выделенного: тогда выделение ни на что не влияет.",
                                 "Turn off to simply dictate over a selection: then selecting changes nothing.");
        ExamplesLabel.Text = L.T("Что можно сказать", "What you can say");
        Examples.Text = L.T("«сделай короче»  ·  «исправь ошибки»  ·  «перепиши вежливее»\n«переведи на английский»  ·  «сделай списком»  ·  «добавь заголовок»",
                            "\"make it shorter\"  ·  \"fix the mistakes\"  ·  \"make it more polite\"\n\"translate into English\"  ·  \"make it a list\"  ·  \"add a title\"\n(said in Russian)");
        EveryTakeBox.Content = L.T("Править каждую диктовку, без команды", "Edit every take, without a command");
        EveryTakeBox.IsChecked = _settings.BrainEveryTake;
        EveryTakeHint.Text = L.T("Удобно с быстрым облачным сервисом: нейросеть причёсывает всё подряд.",
                                 "Handy with a fast cloud service: the model tidies up everything.");
        PromptExpander.Header = L.T("Инструкция для этого режима", "Instructions for this mode");
        PromptBox.Text = _settings.EffectiveCleanupPrompt;
        UpdateBrainTexts();

        AboutHeading.Text = L.T("О программе", "About");
        About.Text = L.T($"Гига Писарь {PisarApp.Version}. Распознавание идёт на вашем компьютере моделью GigaAM от Сбера, звук никуда не отправляется.",
                         $"Giga Pisar {PisarApp.Version}. Speech is recognized on your computer by Sber's GigaAM model; audio never leaves it.");
        ModelPath.Text = L.T("Модель: ", "Model: ") + Settings.ModelDir;
        CodeLink.Text = L.T("исходный код", "source code");
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

    /// <summary>Shows only what the chosen Brain needs: the server fields, the delete button, the every-take option.</summary>
    private void UpdateBrainTexts()
    {
        var b = _settings.Brain;
        BrainStatus.Text = b switch
        {
            BrainSource.Local => L.T("Работает без интернета. Пока нужен, занимает около 2,5 ГБ памяти, через 15 минут без дела выгружается.",
                                     "Works offline. Takes about 2.5 GB of memory while needed, unloads after 15 idle minutes."),
            BrainSource.Off => L.T("Текст вставляется как распознан.", "Text is inserted as recognized."),
            _ => "",
        };
        BrainStatus.Visibility = BrainStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerPanel.Visibility = b == BrainSource.Server ? Visibility.Visible : Visibility.Collapsed;
        DeleteBrainButton.Visibility = LocalBrain.Downloaded && b != BrainSource.Local ? Visibility.Visible : Visibility.Collapsed;
        var every = b == BrainSource.Off ? Visibility.Collapsed : Visibility.Visible;
        // Editing a selection is done by the Brain: say which one, or that it has to be turned on first.
        bool ready = b switch { BrainSource.Local => LocalBrain.Downloaded, BrainSource.Server => Brain.ServerConfigured(_settings), _ => false };
        string host = SpeechCleanup.HostOf(_settings.CleanupEndpointUrl);
        EditBrainLine.Text = !ready
            ? L.T("Текст переписывает нейросеть, поэтому для правки нужен Мозг. Сейчас он не настроен.",
                  "An AI model rewrites the text, so editing needs the Brain. It is not set up yet.")
            : b == BrainSource.Local
                ? L.T("Переписывает Мозг на этом компьютере, без интернета.", "Rewritten by the Brain on this computer, offline.")
                : L.T($"Переписывает Мозг в облаке: {host}. Выделенный текст уходит туда, звук нет.",
                      $"Rewritten by the Brain in the cloud: {host}. The selected text goes there, audio does not.");
        GoBrainButton.Content = ready ? L.T("Мозг…", "Brain…") : L.T("Настроить Мозг", "Set up the Brain");
        EveryTakeBox.Visibility = every;
        EveryTakeHint.Visibility = every;
        PromptExpander.Visibility = b != BrainSource.Off && _settings.BrainEveryTake ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Brain_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BrainBox.SelectedItem is not ComboBoxItem item) return;
        var source = (BrainSource)item.Tag;
        if (source == _settings.Brain) return;
        await _selectBrain(source);
        Localize();   // the choice may have been cancelled (download declined)
        if (source == BrainSource.Server) ServerPanel.FocusKey();
    }

    private void Prompt_LostFocus(object sender, RoutedEventArgs e)
    {
        var prompt = PromptBox.Text.Trim();
        // Our default (in any language) is stored as empty, so it keeps following the interface language.
        _settings.CleanupPrompt = prompt.Length == 0 || SpeechCleanup.IsDefaultPrompt(prompt) ? "" : prompt;
        _apply();
    }

    private void GoBrain_Click(object sender, RoutedEventArgs e) => ShowPage(Page.Brain);

    private void Selection_Click(object sender, RoutedEventArgs e)
    {
        _settings.BrainOnSelection = SelectionBox.IsChecked == true;
        _apply();
    }

    private void EveryTake_Click(object sender, RoutedEventArgs e)
    {
        _settings.BrainEveryTake = EveryTakeBox.IsChecked == true;
        _apply();
        UpdateBrainTexts();
    }

    private void DeleteBrain_Click(object sender, RoutedEventArgs e)
    {
        LocalBrain.DeleteModel();
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
