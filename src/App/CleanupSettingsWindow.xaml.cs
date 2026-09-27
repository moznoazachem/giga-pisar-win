// Brain on a server: pick the service, paste the key, the model is chosen for you.
// Most people have a key and a service name, not an API address, so the address
// field only shows up for "own server". Saving switches the Brain to the server.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GigaPisar.App;

public partial class CleanupSettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Action _apply;
    private readonly DispatcherTimer _keyPause = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CancellationTokenSource? _loading;
    private bool _init = true;

    public CleanupSettingsWindow(Settings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;
        InitializeComponent();

        Title = L.T("Мозг на сервере", "Brain on a server");
        Heading.Text = L.T("Мозг в облаке или на своём сервере", "Brain in the cloud or on your server");
        Intro.Text = L.T("Выберите сервис и вставьте ключ, модель Писарь подберёт сам. Распознавание остаётся на компьютере, звук никуда не уходит, на сервер отправляется только готовый текст.",
            "Pick the service and paste the key; Pisar chooses the model. Recognition stays on this computer and audio never leaves it; only the recognized text is sent.");
        ProviderLabel.Text = L.T("Сервис", "Service");
        KeyLabel.Text = L.T("Ключ API", "API key");
        KeyHint.Text = L.T("Хранится в зашифрованном виде, прочитать его может только ваша учётная запись Windows. ",
            "Stored encrypted; only your Windows account can read it. ");
        UrlLabel.Text = L.T("Адрес сервера", "Server address");
        UrlHint.Text = L.T("OpenAI-совместимый API, например http://localhost:1234/v1 для LM Studio или http://localhost:11434/v1 для Ollama. Ключ для своего сервера обычно не нужен.",
            "An OpenAI-compatible API, e.g. http://localhost:1234/v1 for LM Studio or http://localhost:11434/v1 for Ollama. Your own server usually needs no key.");
        ModelLabel.Text = L.T("Модель", "Model");
        RefreshButton.Content = L.T("Обновить список", "Reload list");
        PromptExpander.Header = L.T("Инструкция для режима «каждая диктовка»", "Instructions for \"edit every take\"");
        CancelButton.Content = L.T("Отмена", "Cancel");
        SaveButton.Content = L.T("Сохранить и включить", "Save and turn on");

        foreach (var p in BrainProviders.All)
            ProviderBox.Items.Add(new ComboBoxItem { Content = BrainProviders.Title(p), Tag = p });
        var current = settings.CleanupEndpointUrl.Length > 0 ? BrainProviders.FromUrl(settings.CleanupEndpointUrl) : BrainProviders.DeepSeek;
        ProviderBox.SelectedIndex = Array.IndexOf(BrainProviders.All, current);

        UrlBox.Text = current.IsCustom ? settings.CleanupEndpointUrl : "";
        KeyBox.Password = settings.CleanupApiKey;
        ModelBox.Text = settings.CleanupModel;
        PromptBox.Text = settings.EffectiveCleanupPrompt;
        UpdateProviderUi();
        _init = false;

        _keyPause.Tick += (_, _) => { _keyPause.Stop(); _ = LoadModelsAsync(pickDefault: true); };
        Loaded += (_, _) =>
        {
            if (KeyBox.Password.Length == 0) KeyBox.Focus();
            else if (!Provider.IsCustom) _ = LoadModelsAsync(pickDefault: ModelBox.Text.Length == 0);
        };
        Closed += (_, _) => _loading?.Cancel();
    }

    private BrainProvider Provider => (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as BrainProvider ?? BrainProviders.Custom;
    private string Endpoint => Provider.IsCustom ? UrlBox.Text.Trim() : Provider.BaseUrl;

    private void UpdateProviderUi()
    {
        var p = Provider;
        UrlPanel.Visibility = p.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        KeysLink.IsEnabled = !p.IsCustom;
        KeysLinkText.Text = p.IsCustom ? "" : L.T($"Где взять ключ {p.Name}", $"Get a {p.Name} key");
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        UpdateProviderUi();
        ModelBox.Items.Clear();
        ModelBox.Text = "";
        Status.Text = "";
        if (!Provider.IsCustom && KeyBox.Password.Length > 0) _ = LoadModelsAsync(pickDefault: true);
    }

    /// <summary>A pasted key tells which service it is from; then the model list loads by itself.</summary>
    private void Key_Changed(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        var guess = BrainProviders.FromKey(KeyBox.Password);
        if (guess != null && guess != Provider && !(Provider.IsCustom && UrlBox.Text.Trim().Length > 0))
        {
            _init = true;
            ProviderBox.SelectedIndex = Array.IndexOf(BrainProviders.All, guess);
            UpdateProviderUi();
            ModelBox.Items.Clear();
            ModelBox.Text = "";
            _init = false;
            Status.Text = L.T($"Похоже на ключ {guess.Name}, выбрал его.", $"Looks like a {guess.Name} key, selected it.");
        }
        _keyPause.Stop();
        if (KeyBox.Password.Trim().Length > 0) _keyPause.Start();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadModelsAsync(pickDefault: ModelBox.Text.Trim().Length == 0);

    private async Task LoadModelsAsync(bool pickDefault)
    {
        var p = Provider;
        if (!SpeechCleanup.TryGetCompletionsUrl(Endpoint, out _))
        {
            Status.Text = L.T("Впишите адрес сервера.", "Enter the server address.");
            return;
        }
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        Status.Text = L.T("Загружаю список моделей…", "Loading the model list…");
        try
        {
            var models = BrainProviders.ChatModels(await SpeechCleanup.GetModelsAsync(Endpoint, KeyBox.Password, cts.Token))
                .Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            if (cts.IsCancellationRequested) return;
            var typed = ModelBox.Text.Trim();
            ModelBox.Items.Clear();
            foreach (var m in models) ModelBox.Items.Add(m);
            ModelBox.Text = pickDefault || typed.Length == 0 ? BrainProviders.PickDefault(p, models) ?? "" : typed;
            Status.Text = L.T($"Ключ подошёл. Моделей: {models.Count}, выбрана {ModelBox.Text}. Можно сохранять.",
                              $"The key works. {models.Count} models, {ModelBox.Text} selected. Ready to save.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Write($"brain models failed: {ex.GetType().Name}: {ex.Message}");
            bool keyRejected = ex is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
            if (!p.IsCustom && ModelBox.Text.Trim().Length == 0 && !keyRejected)
                ModelBox.Text = p.PreferredModels.FirstOrDefault() ?? "";
            Status.Text = keyRejected
                ? L.T($"{p.Name} не принял ключ. Проверьте, что ключ от этого сервиса и скопирован целиком.",
                      $"{p.Name} rejected the key. Check that it is for this service and copied in full.")
                : L.T("Список моделей не загрузился: " + ex.Message, "Could not load the model list: " + ex.Message);
        }
        finally
        {
            if (_loading == cts) RefreshButton.IsEnabled = true;
        }
    }

    private void KeysLink_Click(object sender, RoutedEventArgs e)
    {
        if (Provider.KeysUrl.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(Provider.KeysUrl) { UseShellExecute = true }); } catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = Endpoint;
        var model = ModelBox.Text.Trim();
        var prompt = PromptBox.Text.Trim();
        if (!SpeechCleanup.TryGetCompletionsUrl(endpoint, out _))
        {
            Status.Text = L.T("Впишите адрес сервера вида https://… или http://…", "Enter a server address like https://… or http://…");
            return;
        }
        if (!Provider.IsCustom && KeyBox.Password.Trim().Length == 0)
        {
            Status.Text = L.T("Вставьте ключ.", "Paste the key.");
            KeyBox.Focus();
            return;
        }
        if (model.Length == 0)
        {
            Status.Text = L.T("Выберите модель или нажмите «Обновить список».", "Pick a model or press Reload list.");
            return;
        }

        if (SpeechCleanup.IsInsecureRemote(endpoint) &&
            MessageBox.Show(this,
                L.T("Адрес начинается с http://, а сервер не на этом компьютере и не в домашней сети. Текст и ключ пойдут по интернету без шифрования. Лучше использовать https://. Всё равно сохранить?",
                    "The address starts with http:// and the server is neither on this computer nor on your home network. Text and key will cross the internet unencrypted. Prefer https://. Save anyway?"),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        LocalBrain.Stop();
        _settings.Brain = BrainSource.Server;
        _settings.CleanupEndpointUrl = endpoint;
        _settings.CleanupApiKey = KeyBox.Password.Trim();
        _settings.CleanupModel = model;
        // Our default (in any language) is stored as empty, so it keeps following the interface language.
        _settings.CleanupPrompt = prompt.Length == 0 || SpeechCleanup.IsDefaultPrompt(prompt) ? "" : prompt;
        _apply();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
