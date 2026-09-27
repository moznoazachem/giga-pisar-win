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

        Title = L.T("Очистка распознанной речи", "Speech cleanup");
        Heading.Text = Title;
        Intro.Text = L.T("После локального распознавания текст отправляется на выбранный сервер для исправления. Звук не отправляется.",
            "After local recognition, the text is sent to your chosen server for correction. Audio is not sent.");
        EnabledBox.Content = L.T("Очищать распознанный текст", "Clean recognized text");
        UrlLabel.Text = L.T("Адрес сервера (URL)", "Endpoint URL");
        UrlHint.Text = L.T("Адрес OpenAI-совместимого API, например http://127.0.0.1:12345/v1. Путь /chat/completions добавляется автоматически.",
            "OpenAI-compatible API URL, such as http://127.0.0.1:12345/v1. /chat/completions is appended automatically.");
        KeyLabel.Text = L.T("API key (необязательно)", "API key (optional)");
        KeyHint.Text = L.T("Передаётся как Bearer-токен. Ключ сохраняется в локальном файле настроек.",
            "Sent as a Bearer token. The key is stored in the local settings file.");
        ModelLabel.Text = L.T("Модель", "Model");
        RefreshButton.Content = L.T("Обновить", "Refresh");
        PromptLabel.Text = L.T("Промпт очистки", "Cleanup prompt");
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
