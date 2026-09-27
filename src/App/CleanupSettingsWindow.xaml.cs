using System.Windows;

namespace GigaPisar.App;

public partial class CleanupSettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Action _apply;

    public CleanupSettingsWindow(Settings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;
        InitializeComponent();
        MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 32);
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);

        Title = L.T("Мозг", "Brain");
        Heading.Text = L.T("Мозг: правка текста нейросетью", "Brain: AI text cleanup");
        Intro.Text = L.T("Мозг убирает слова-паразиты, повторы и оговорки и расставляет знаки перед вставкой. Он работает на сервере, который вы укажете: своём или облачном, с OpenAI-совместимым API. Распознавание остаётся на компьютере, звук никуда не уходит, на сервер отправляется только готовый текст.",
            "The Brain removes filler words, repeats and false starts and fixes punctuation before the text is inserted. It runs on a server you choose, your own or a cloud one, with an OpenAI-compatible API. Recognition stays on this computer and audio never leaves it; only the recognized text is sent.");
        EnabledBox.Content = L.T("Включить Мозг", "Turn on the Brain");
        UrlLabel.Text = L.T("Адрес сервера (URL)", "Endpoint URL");
        UrlHint.Text = L.T("Адрес OpenAI-совместимого API, например http://127.0.0.1:12345/v1. Путь /chat/completions добавляется автоматически.",
            "OpenAI-compatible API URL, such as http://127.0.0.1:12345/v1. /chat/completions is appended automatically.");
        KeyLabel.Text = L.T("API key (необязательно)", "API key (optional)");
        KeyHint.Text = L.T("Передаётся как Bearer-токен. Хранится в файле настроек в зашифрованном виде, прочитать его может только ваша учётная запись Windows.",
            "Sent as a Bearer token. Stored encrypted in the settings file; only your Windows account can read it.");
        ModelLabel.Text = L.T("Модель", "Model");
        RefreshButton.Content = L.T("Обновить", "Refresh");
        PromptLabel.Text = L.T("Инструкция для Мозга", "Instructions for the Brain");
        CancelButton.Content = L.T("Отмена", "Cancel");
        SaveButton.Content = L.T("Сохранить", "Save");

        EnabledBox.IsChecked = settings.CleanupEnabled;
        UrlBox.Text = settings.CleanupEndpointUrl;
        KeyBox.Password = settings.CleanupApiKey;
        ModelBox.Text = settings.CleanupModel;
        PromptBox.Text = settings.CleanupPrompt;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = UrlBox.Text.Trim();
        var model = ModelBox.Text.Trim();
        var prompt = PromptBox.Text.Trim();
        if (EnabledBox.IsChecked == true &&
            (!SpeechCleanup.TryGetCompletionsUrl(endpoint, out _) || model.Length == 0 || prompt.Length == 0))
        {
            MessageBox.Show(this,
                L.T("Укажите корректный HTTP(S) адрес сервера, модель и промпт.", "Enter a valid HTTP(S) endpoint URL, model, and prompt."),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (EnabledBox.IsChecked == true && SpeechCleanup.IsInsecureRemote(endpoint) &&
            MessageBox.Show(this,
                L.T("Адрес начинается с http://, а сервер не на этом компьютере и не в домашней сети. Текст и ключ пойдут по интернету без шифрования. Лучше использовать https://. Всё равно сохранить?",
                    "The address starts with http:// and the server is neither on this computer nor on your home network. Text and key will cross the internet unencrypted. Prefer https://. Save anyway?"),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _settings.CleanupEnabled = EnabledBox.IsChecked == true;
        _settings.CleanupEndpointUrl = endpoint;
        _settings.CleanupApiKey = KeyBox.Password;
        _settings.CleanupModel = model;
        _settings.CleanupPrompt = prompt;
        _apply();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var models = await SpeechCleanup.GetModelsAsync(UrlBox.Text, KeyBox.Password, CancellationToken.None);
            var selected = ModelBox.Text;
            ModelBox.Items.Clear();
            foreach (var model in models) ModelBox.Items.Add(model);
            ModelBox.Text = selected;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                L.T("Не удалось получить список моделей: ", "Could not load models: ") + ex.Message,
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshButton.IsEnabled = true; }
    }
}
