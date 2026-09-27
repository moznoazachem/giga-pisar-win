// Progress window for one-time downloads: the speech model on first run, the local Brain on request.

using System.Windows;

namespace GigaPisar.App;

public partial class DownloadWindow : Window
{
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _done;
    private readonly Func<IProgress<ModelDownloader.Progress>, CancellationToken, Task> _work;
    private readonly string _spaceNeeded;

    /// <summary>The speech model download into targetDir.</summary>
    public DownloadWindow(string targetDir)
        : this(L.T("Скачиваю модель распознавания", "Downloading the speech model"),
               L.T("Это делается один раз. Модель GigaAM от Сбера, около 300 МБ. После загрузки Писарь работает без интернета: звук никуда не отправляется.",
                   "A one-time step. Sber's GigaAM model, about 300 MB. Afterwards Pisar works offline: audio never leaves your computer."),
               (progress, ct) => ModelDownloader.DownloadAsync(targetDir, progress, ct),
               L.T("1 ГБ", "1 GB"))
    {
    }

    public DownloadWindow(string heading, string intro, Func<IProgress<ModelDownloader.Progress>, CancellationToken, Task> work,
        string? spaceNeeded = null)
    {
        _work = work;
        _spaceNeeded = spaceNeeded ?? L.T("2,5 ГБ", "2.5 GB");
        InitializeComponent();
        Title = L.T("Гига Писарь", "Giga Pisar");
        Heading.Text = heading;
        Intro.Text = intro;
        RetryButton.Content = L.T("Повторить", "Retry");
        CancelButton.Content = L.T("Отмена", "Cancel");
    }

    /// <summary>Shows the window, runs the download, returns true on success.</summary>
    public Task<bool> RunAsync()
    {
        _done = new TaskCompletionSource<bool>();
        Closed += (_, _) => _done.TrySetResult(false);
        Show();
        _ = StartAsync();
        return _done.Task;
    }

    private async Task StartAsync()
    {
        RetryButton.Visibility = Visibility.Collapsed;
        Bar.IsIndeterminate = true;
        Status.Text = L.T("Соединяюсь…", "Connecting…");
        _cts = new CancellationTokenSource();
        var progress = new Progress<ModelDownloader.Progress>(p =>
        {
            switch (p.Stage)
            {
                case "download":
                    Bar.IsIndeterminate = p.Total <= 0;
                    if (p.Total > 0)
                    {
                        Bar.Value = 100.0 * p.Received / p.Total;
                        Status.Text = L.T($"Скачано {p.Received / 1048576} из {p.Total / 1048576} МБ", $"Downloaded {p.Received / 1048576} of {p.Total / 1048576} MB");
                    }
                    else Status.Text = L.T($"Скачано {p.Received / 1048576} МБ", $"Downloaded {p.Received / 1048576} MB");
                    break;
                case "unpack":
                    Bar.IsIndeterminate = true;
                    Status.Text = L.T("Распаковываю…", "Unpacking…");
                    break;
                case "verify":
                    Bar.IsIndeterminate = true;
                    Status.Text = L.T("Проверяю файл…", "Checking the file…");
                    break;
            }
        });
        try
        {
            await _work(progress, _cts.Token);
            _done?.TrySetResult(true);
            Close();
        }
        catch (OperationCanceledException)
        {
            _done?.TrySetResult(false);
            Close();
        }
        catch (Exception e)
        {
            Log.Write($"download failed: {e}");
            Bar.IsIndeterminate = false;
            var kind = (e as ModelDownloadException)?.Kind ?? DownloadFailure.Network;
            Status.Text = kind switch
            {
                DownloadFailure.NoSpace => L.T($"Мало места на диске: нужно около {_spaceNeeded} свободных. Освободите место и повторите.",
                                               $"Not enough disk space: about {_spaceNeeded} is needed. Free some space and retry."),
                DownloadFailure.Corrupt => L.T("Скачанный архив повреждён или подменён. Попробуйте ещё раз позже.",
                                               "The downloaded archive is damaged or does not match. Try again later."),
                _ => L.T("Не получилось скачать. Проверьте интернет и попробуйте ещё раз.",
                         "Download failed. Check your connection and try again."),
            };
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = StartAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _done?.TrySetResult(false);
        Close();
    }
}
