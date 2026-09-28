// The server Brain, inline in Settings: pick the service, paste the key, the model
// is chosen for you. Most people have a key and a service name, not an API address,
// so the address row only shows up for "own server". Like the rest of Settings,
// every valid change is saved at once; there is no separate Save step.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GigaPisar.App;

public partial class BrainServerPanel : UserControl
{
    private Settings _settings = new();
    private Action _apply = () => { };
    private readonly DispatcherTimer _keyPause = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CancellationTokenSource? _loading;
    private bool _init = true;

    /// <summary>Raised after the server settings were saved (address, key or model changed).</summary>
    public event Action? Saved;

    public BrainServerPanel()
    {
        InitializeComponent();
        _keyPause.Tick += (_, _) => { _keyPause.Stop(); _ = LoadModelsAsync(pickDefault: true); };
        Unloaded += (_, _) =>
        {
            _keyPause.Stop();   // no key goes anywhere after the window is closed
            _loading?.Cancel();
        };
    }

    public void Bind(Settings settings, Action apply)
    {
        _settings = settings;
        _apply = apply;
        _init = true;
        _keyPause.Stop();
        _loading?.Cancel();
        ProviderLabel.Text = L.T("Сервис", "Service");
        KeyLabel.Text = L.T("Ключ API", "API key");
        UrlLabel.Text = L.T("Адрес", "Address");
        ModelLabel.Text = L.T("Модель", "Model");
        RefreshButton.Content = L.T("Обновить", "Reload");
        UrlBox.ToolTip = L.T("OpenAI-совместимый API, например http://localhost:1234/v1 (LM Studio) или http://localhost:11434/v1 (Ollama)",
                             "An OpenAI-compatible API, e.g. http://localhost:1234/v1 (LM Studio) or http://localhost:11434/v1 (Ollama)");
        KeyBox.ToolTip = L.T("Хранится в зашифрованном виде, прочитать его может только ваша учётная запись Windows",
                             "Stored encrypted; only your Windows account can read it");

        ProviderBox.Items.Clear();
        foreach (var p in BrainProviders.All)
            ProviderBox.Items.Add(new ComboBoxItem { Content = BrainProviders.Title(p), Tag = p });
        var current = settings.CleanupEndpointUrl.Length > 0 ? BrainProviders.FromUrl(settings.CleanupEndpointUrl) : BrainProviders.DeepSeek;
        ProviderBox.SelectedIndex = Array.IndexOf(BrainProviders.All, current);
        UrlBox.Text = current.IsCustom ? settings.CleanupEndpointUrl : "";
        KeyBox.Password = settings.CleanupApiKey;
        ModelBox.Text = settings.CleanupModel;
        Status.Text = Brain.ServerConfigured(settings)
            ? L.T($"Текст (не звук) уходит на {SpeechCleanup.HostOf(settings.CleanupEndpointUrl)}, модель {settings.CleanupModel}.",
                  $"Text (not audio) goes to {SpeechCleanup.HostOf(settings.CleanupEndpointUrl)}, model {settings.CleanupModel}.")
            : L.T("Выберите сервис и вставьте ключ, модель подберётся сама.", "Pick the service and paste the key; the model is chosen for you.");
        UpdateProviderUi();
        _lastProvider = current;
        _init = false;
    }

    private BrainProvider _lastProvider = BrainProviders.DeepSeek;

    public void FocusKey()
    {
        if (KeyBox.Password.Length == 0) KeyBox.Focus();
    }

    private BrainProvider Provider => (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as BrainProvider ?? BrainProviders.Custom;
    private string Endpoint => Provider.IsCustom ? UrlBox.Text.Trim() : Provider.BaseUrl;

    private void UpdateProviderUi()
    {
        var p = Provider;
        var url = p.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        UrlLabel.Visibility = url;
        UrlBox.Visibility = url;
        KeysLink.IsEnabled = !p.IsCustom;
        KeysLinkText.Text = p.IsCustom ? L.T("Для своего сервера ключ обычно не нужен", "Your own server usually needs no key")
                                       : L.T($"Где взять ключ {p.Name}", $"Get a {p.Name} key");
    }

    private void ClearModels()
    {
        ModelBox.Items.Clear();
        ModelBox.Text = "";
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        // A cloud key must never travel to an address typed for "own server" (and back).
        if (Provider.IsCustom != _lastProvider.IsCustom) { _init = true; KeyBox.Password = ""; _init = false; }
        _lastProvider = Provider;
        _loading?.Cancel();
        _keyPause.Stop();
        UpdateProviderUi();
        ClearModels();
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
            _lastProvider = guess;
            _loading?.Cancel();
            UpdateProviderUi();
            ClearModels();
            _init = false;
            Status.Text = L.T($"Похоже на ключ {guess.Name}, выбрал его.", $"Looks like a {guess.Name} key, selected it.");
        }
        _keyPause.Stop();
        if (KeyBox.Password.Trim().Length > 0) _keyPause.Start();
        else _loading?.Cancel();
    }

    private void Url_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_init || !SpeechCleanup.TryGetCompletionsUrl(Endpoint, out _)) return;
        _ = LoadModelsAsync(pickDefault: ModelBox.Text.Trim().Length == 0);
    }

    private void Model_LostFocus(object sender, RoutedEventArgs e) => TrySave();

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        // SelectionChanged fires before Text follows the new item.
        Dispatcher.BeginInvoke(() =>
        {
            if (!TrySave()) return;
            _loading?.Cancel();
            _loading = new CancellationTokenSource();
            _ = ProbeAsync(_loading.Token);
        }, DispatcherPriority.Background);
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
        _loading?.Dispose();
        var cts = _loading = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        Status.Text = L.T("Проверяю ключ и загружаю модели…", "Checking the key and loading models…");
        try
        {
            var models = BrainProviders.ChatModels(await SpeechCleanup.GetModelsAsync(Endpoint, KeyBox.Password, cts.Token))
                .Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            if (cts.IsCancellationRequested) return;
            var typed = ModelBox.Text.Trim();
            _init = true;
            ModelBox.Items.Clear();
            foreach (var m in models) ModelBox.Items.Add(m);
            ModelBox.Text = pickDefault || typed.Length == 0 ? BrainProviders.PickDefault(p, models) ?? "" : typed;
            _init = false;
            if (TrySave()) await ProbeAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _init = false;
            Log.Write($"brain models failed: {ex.GetType().Name}: {ex.Message}");
            bool keyRejected = ex is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
            if (keyRejected)
            {
                Status.Text = L.T($"{p.Name} не принял ключ. Проверьте, что ключ от этого сервиса и скопирован целиком.",
                                  $"{p.Name} rejected the key. Check that it is for this service and copied in full.");
            }
            else
            {
                if (!p.IsCustom && ModelBox.Text.Trim().Length == 0) ModelBox.Text = p.PreferredModels.FirstOrDefault() ?? "";
                Status.Text = L.T("Список моделей не загрузился: " + ex.Message, "Could not load the model list: " + ex.Message);
                TrySave();
            }
        }
        finally
        {
            if (_loading == cts) RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Asks the model for one word. A key that lists models may still be unable to chat
    /// (no API balance, no access to the model, a blocked country): say so right here.
    /// </summary>
    private async Task ProbeAsync(CancellationToken ct)
    {
        string endpoint = Endpoint, model = ModelBox.Text.Trim(), key = KeyBox.Password.Trim();
        Status.Text = L.T($"Ключ подошёл, проверяю модель {model}…", $"The key works, checking model {model}…");
        try
        {
            await SpeechCleanup.ProbeAsync(endpoint, key, model, ct);
            if (ct.IsCancellationRequested) return;
            Status.Text = L.T($"Всё работает, сохранено. Модель {model}, текст (не звук) уходит на {SpeechCleanup.HostOf(endpoint)}.",
                              $"All set and saved. Model {model}; text (not audio) goes to {SpeechCleanup.HostOf(endpoint)}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            Status.Text = L.T($"Ключ принят, но нейросеть не отвечает: {e.Message}.",
                              $"The key is accepted, but the model does not answer: {e.Message}.");
        }
    }

    /// <summary>Saves address, key and model when they make a usable set. Returns true when saved.</summary>
    private bool TrySave()
    {
        if (_init) return false;
        var endpoint = Endpoint;
        var model = ModelBox.Text.Trim();
        var key = KeyBox.Password.Trim();
        if (!Brain.ServerConfigured(endpoint, model, key)) return false;
        if (endpoint == _settings.CleanupEndpointUrl && key == _settings.CleanupApiKey && model == _settings.CleanupModel)
            return true;
        _settings.CleanupEndpointUrl = endpoint;
        _settings.CleanupApiKey = key;
        _settings.CleanupModel = model;
        _apply();
        if (SpeechCleanup.IsInsecureRemote(endpoint))
            Status.Text = L.T("Внимание: адрес с http://, а сервер не на этом компьютере и не в домашней сети. Текст и ключ идут по интернету без шифрования, лучше https://.",
                              "Warning: an http:// address outside this computer and your home network. Text and key cross the internet unencrypted; prefer https://.");
        Saved?.Invoke();
        return true;
    }

    private void KeysLink_Click(object sender, RoutedEventArgs e)
    {
        if (Provider.KeysUrl.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(Provider.KeysUrl) { UseShellExecute = true }); } catch { }
    }
}
