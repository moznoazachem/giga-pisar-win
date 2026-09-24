// "A new version is out": notes, an Update button, download progress, then the
// silent installer takes over.

using System.Windows;

namespace GigaPisar.App;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly Action _quit;
    private CancellationTokenSource? _cts;

    public UpdateWindow(UpdateInfo info, Action quit)
    {
        _info = info;
        _quit = quit;
        InitializeComponent();
        Title = L.T("Гига Писарь", "Giga Pisar");
        Heading.Text = L.T($"Вышла версия {info.Version}", $"Version {info.Version} is out");
        var notes = L.Russian || string.IsNullOrWhiteSpace(info.NotesEn) ? info.Notes : info.NotesEn;
        Notes.Text = string.IsNullOrWhiteSpace(notes)
            ? L.T($"У вас {PisarApp.Version}. Обновление скачается и установится само, Писарь перезапустится.",
                  $"You have {PisarApp.Version}. The update downloads and installs by itself, then Pisar restarts.")
            : notes;
        LaterButton.Content = L.T("Позже", "Later");
        UpdateButton.Content = L.T("Обновить", "Update");
        Closed += (_, _) => _cts?.Cancel();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        LaterButton.Content = L.T("Отмена", "Cancel");
        Bar.Visibility = Visibility.Visible;
        Status.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        Status.Text = L.T("Скачиваю…", "Downloading…");
        _cts = new CancellationTokenSource();
        var progress = new Progress<(long received, long total)>(p =>
        {
            if (p.total > 0)
            {
                Bar.IsIndeterminate = false;
                Bar.Value = 100.0 * p.received / p.total;
                Status.Text = L.T($"Скачано {p.received / 1048576} из {p.total / 1048576} МБ", $"Downloaded {p.received / 1048576} of {p.total / 1048576} MB");
            }
        });
        try
        {
            var path = await Updater.DownloadAsync(_info, progress, _cts.Token);
            Status.Text = L.T("Устанавливаю…", "Installing…");
            Bar.IsIndeterminate = true;
            Updater.Install(path);
            _quit();
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            Log.Write($"update failed: {ex}");
            Bar.IsIndeterminate = false;
            Status.Text = L.T("Не получилось скачать обновление. Попробуйте позже или скачайте с сайта.",
                              "Could not download the update. Try later or download it from the website.");
            UpdateButton.IsEnabled = true;
            LaterButton.Content = L.T("Позже", "Later");
        }
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }
}
