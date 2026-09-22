// Progress window for the one-time model download.

using System.Windows;

namespace GigaPisar.App;

public partial class DownloadWindow : Window
{
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _done;
    private string _target = "";

    public DownloadWindow()
    {
        InitializeComponent();
        Title = L.T("Гига Писарь", "Giga Pisar");
        Heading.Text = L.T("Скачиваю модель распознавания", "Downloading the speech model");
        Intro.Text = L.T("Это делается один раз. Модель GigaAM от Сбера, около 300 МБ. После загрузки Писарь работает без интернета: звук никуда не отправляется.",
                         "A one-time step. Sber's GigaAM model, about 300 MB. Afterwards Pisar works offline: audio never leaves your computer.");
        RetryButton.Content = L.T("Повторить", "Retry");
        CancelButton.Content = L.T("Отмена", "Cancel");
    }

    /// <summary>Shows the window, downloads into targetDir, returns true on success.</summary>
    public Task<bool> RunAsync(string targetDir)
    {
        _target = targetDir;
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
            }
        });
        try
        {
            await ModelDownloader.DownloadAsync(_target, progress, _cts.Token);
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
            Status.Text = L.T("Не получилось скачать. Проверьте интернет и попробуйте ещё раз.", "Download failed. Check your connection and try again.");
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = StartAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _done?.TrySetResult(false);
        Close();
    }
}
