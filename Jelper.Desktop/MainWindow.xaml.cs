using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Jelper.Desktop.Infrastructure;
using Jelper.Desktop.Services;
using WinForms = System.Windows.Forms;
using DataFormats = System.Windows.DataFormats;
using WpfMessageBox = System.Windows.MessageBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace Jelper.Desktop;

public partial class MainWindow : Window, ILogSink
{
    private readonly ImageOperations _operations;
    private readonly List<ConversionOption> _conversionOptions = new()
    {
        new("WEBP", "*.webp", "PNG", ".png"),
        new("WEBP", "*.webp", "JPG", ".jpg"),
        new("JPG", "*.jpg", "PNG", ".png"),
        new("PNG", "*.png", "JPG", ".jpg")
    };
    private readonly Dictionary<OperationPanel, UIElement> _operationViews;
    private OperationPanel _activePanel = OperationPanel.None;
    private bool _isBusy;
    private readonly ObservableCollection<FileProcessingItem> _fileProgressItems = new();
    private readonly Dictionary<string, FileProcessingItem> _fileProgressLookup = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _operationCancellation;
    private DateTimeOffset? _operationStartTimestamp;
    private int _completedFilesInOperation;
    private int _totalFilesInOperation;
    private readonly DispatcherTimer _etaUpdateTimer;
    private DateTimeOffset? _currentFileStartTimestamp;
    private double _lastAverageSeconds;

    public MainWindow()
    {
        InitializeComponent();

        ProcessingFilesList.ItemsSource = _fileProgressItems;
        CancelOperationButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Collapsed;

        _etaUpdateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _etaUpdateTimer.Tick += (_, _) => RefreshEtaText();

        _operationViews = new Dictionary<OperationPanel, UIElement>
        {
            { OperationPanel.Convert, ConvertForm },
            { OperationPanel.Trim, TrimForm },
            { OperationPanel.Resize, ResizeForm },
            { OperationPanel.Watermark, WatermarkForm }
        };

        _operations = new ImageOperations(this, GetImagesFolderPath);

        InitializeConversionControls();

        var savedPath = UserSettings.LoadImagesFolderPath();
        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            SetImagesFolder(savedPath, logChange: false);
            AppendLog($"Images folder set to {savedPath}");
        }
    }

    public void Info(string message) => AppendLog(message);
    public void Error(string message) => AppendLog(message, isError: true);

    private async void ConvertButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureImagesFolderSelected() ||
            !TryGetSelectedConversion(out var conversion) ||
            !TryGetConversionFiles(conversion, out var files) ||
            !ConfirmOperationCount(files.Count))
        {
            return;
        }

        var description = $"Конвертация {conversion.SourceLabel} → {conversion.TargetLabel}...";
        await RunOperationAsync(description, files, ctx => _operations.ConvertFiles(files, conversion.TargetExtension, ctx));
    }

    private async void TrimButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureImagesFolderSelected() ||
            !TryGetFiles(OperationPanel.Trim, out var files) ||
            !ConfirmOperationCount(files.Count))
        {
            return;
        }

        if (!TryGetPositiveInt(PixelsToTrimTextBox, "Pixels to trim", out var pixels))
        {
            return;
        }

        await RunOperationAsync($"Removing {pixels}px watermark strip...", files, ctx => _operations.RemoveWatermark(pixels, files, ctx));
    }

    private async void ResizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureImagesFolderSelected() ||
            !TryGetFiles(OperationPanel.Resize, out var files) ||
            !ConfirmOperationCount(files.Count))
        {
            return;
        }

        if (!TryGetPositiveInt(ResizeWidthTextBox, "Width", out var width) ||
            !TryGetPositiveInt(ResizeHeightTextBox, "Height", out var height))
        {
            return;
        }

        await RunOperationAsync($"Resizing images to {width}x{height}...", files, ctx => _operations.ResizeImages(width, height, files, ctx));
    }

    private async void WatermarkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureImagesFolderSelected() ||
            !TryGetFiles(OperationPanel.Watermark, out var files) ||
            !ConfirmOperationCount(files.Count))
        {
            return;
        }

        if (!TryGetPositiveInt(WatermarkWidthTextBox, "Width", out var width) ||
            !TryGetPositiveInt(WatermarkHeightTextBox, "Height", out var height))
        {
            return;
        }

        var watermarkPath = WatermarkPathTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(watermarkPath) || !File.Exists(watermarkPath))
        {
            WpfMessageBox.Show(this, "Choose a PNG watermark file first.", "Watermark required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunOperationAsync($"Resizing and watermarking with {Path.GetFileName(watermarkPath)}...",
            files,
            ctx => _operations.ResizeAndWatermark(width, height, watermarkPath, files, ctx));
    }

    private void BrowseFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        BrowseForFolder();
    }

    private void ChooseWatermarkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureImagesFolderSelected())
        {
            return;
        }

        var baseFolder = GetImagesFolderPath();
        var watermarksFolder = Path.Combine(baseFolder, "watermarks");
        Directory.CreateDirectory(watermarksFolder);

        var dialog = new WpfOpenFileDialog
        {
            Title = "Select PNG watermark",
            Filter = "PNG files (*.png)|*.png|All files (*.*)|*.*",
            Multiselect = false,
            InitialDirectory = Directory.Exists(watermarksFolder) ? watermarksFolder : baseFolder
        };

        if (dialog.ShowDialog(this) == true)
        {
            WatermarkPathTextBox.Text = dialog.FileName;
            AppendLog($"Selected watermark: {Path.GetFileName(dialog.FileName)}");
        }
    }

    private void ClearLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void ActionShortcut_OnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureImagesFolderSelected())
        {
            return;
        }

        if (sender is not System.Windows.Controls.Button { Tag: string tag } ||
            !Enum.TryParse(tag, out OperationPanel panel))
        {
            return;
        }

        if (_activePanel == panel && DetailsCard.Visibility == Visibility.Visible)
        {
            return;
        }

        ShowOperationPanel(panel);
    }

    private void HideDetailsButton_OnClick(object sender, RoutedEventArgs e) => HideOperationPanel();

    private void ShowLogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        LogsOverlay.Visibility = Visibility.Visible;
    }

    private void HideLogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        LogsOverlay.Visibility = Visibility.Collapsed;
    }

    private void CancelOperationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation == null || !_isBusy)
        {
            return;
        }

        CancelOperationButton.IsEnabled = false;
        _operationCancellation.Cancel();
        AppendLog("Cancellation requested...");
    }


    private void ShowOperationPanel(OperationPanel panel)
    {
        foreach (var view in _operationViews)
        {
            view.Value.Visibility = view.Key == panel ? Visibility.Visible : Visibility.Collapsed;
        }

        _activePanel = panel;
        DetailsHeader.Text = GetPanelTitle(panel);
        DetailsCard.Visibility = Visibility.Visible;
    }

    private void HideOperationPanel()
    {
        _activePanel = OperationPanel.None;
        DetailsHeader.Text = string.Empty;
        DetailsCard.Visibility = Visibility.Collapsed;

        foreach (var view in _operationViews.Values)
        {
            view.Visibility = Visibility.Collapsed;
        }
    }

    private string GetPanelTitle(OperationPanel panel) => panel switch
    {
        OperationPanel.Convert => "Конвертация",
        OperationPanel.Trim => "Удаление водяного знака",
        OperationPanel.Resize => "Изменение размера",
        OperationPanel.Watermark => "Размер + водяной знак",
        _ => string.Empty
    };

    private bool BrowseForFolder()
    {
        var currentPath = ImagesFolderTextBox.Text?.Trim();
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Выберите папку, содержащую изображения",
            SelectedPath = !string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath)
                ? currentPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            SetImagesFolder(dialog.SelectedPath);
            return true;
        }

        return false;
    }

    private void SetImagesFolder(string? path, bool logChange = true)
    {
        ImagesFolderTextBox.Text = path ?? string.Empty;
        UserSettings.SaveImagesFolderPath(path);

        if (logChange && !string.IsNullOrWhiteSpace(path))
        {
            AppendLog($"Images folder set to {path}");
        }
    }

    private bool EnsureImagesFolderSelected()
    {
        var path = ImagesFolderTextBox.Text?.Trim();
        if (!string.IsNullOrEmpty(path))
        {
            UserSettings.SaveImagesFolderPath(path);
            return true;
        }

        WpfMessageBox.Show(this,
            "Сначала выберите папку с изображениями.",
            "Папка не выбрана",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        return BrowseForFolder();
    }

    private bool TryGetFiles(OperationPanel panel, out IReadOnlyList<string> files)
    {
        files = panel switch
        {
            OperationPanel.Trim => _operations.ListSupportedImageFiles(),
            OperationPanel.Resize => _operations.ListSupportedImageFiles(),
            OperationPanel.Watermark => _operations.ListSupportedImageFiles(),
            _ => throw new InvalidOperationException($"Unsupported panel {panel} for generic file retrieval.")
        };

        if (files.Count > 0)
        {
            return true;
        }

        const string message = "В выбранной папке нет PNG/JPG файлов для обработки.";
        WpfMessageBox.Show(this, message, "Нет подходящих файлов", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private bool TryGetConversionFiles(ConversionOption option, out IReadOnlyList<string> files)
    {
        files = _operations.ListFilesByPattern(option.SourcePattern);
        if (files.Count > 0)
        {
            return true;
        }

        WpfMessageBox.Show(this,
            $"В выбранной папке нет файлов формата {option.SourceLabel}.",
            "Нет подходящих файлов",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private bool TryGetSelectedConversion(out ConversionOption option)
    {
        var source = SourceFormatComboBox.SelectedItem as string;
        var target = TargetFormatComboBox.SelectedItem as string;

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            option = null!;
            WpfMessageBox.Show(this,
                "Выберите исходный и целевой форматы.",
                "Нет формата",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        option = _conversionOptions.FirstOrDefault(o =>
            string.Equals(o.SourceLabel, source, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.TargetLabel, target, StringComparison.OrdinalIgnoreCase))!;

        if (option == null)
        {
            WpfMessageBox.Show(this,
                "Выбранная комбинация форматов не поддерживается.",
                "Неверная комбинация",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private void InitializeConversionControls()
    {
        var sources = _conversionOptions
            .Select(o => o.SourceLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label)
            .ToList();

        SourceFormatComboBox.ItemsSource = sources;
        SourceFormatComboBox.SelectedIndex = sources.Count > 0 ? 0 : -1;

        UpdateTargetFormatChoices();
    }

    private void SourceFormatComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTargetFormatChoices();
    }

    private void UpdateTargetFormatChoices()
    {
        var source = SourceFormatComboBox.SelectedItem as string;
        List<string> targets;

        if (string.IsNullOrWhiteSpace(source))
        {
            targets = new List<string>();
        }
        else
        {
            targets = _conversionOptions
                .Where(o => string.Equals(o.SourceLabel, source, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.TargetLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label)
                .ToList();
        }

        var previousTarget = TargetFormatComboBox.SelectedItem as string;
        TargetFormatComboBox.ItemsSource = targets;

        if (targets.Count == 0)
        {
            TargetFormatComboBox.SelectedIndex = -1;
            return;
        }

        var matchingIndex = targets.FindIndex(label => string.Equals(label, previousTarget, StringComparison.OrdinalIgnoreCase));
        TargetFormatComboBox.SelectedIndex = matchingIndex >= 0 ? matchingIndex : 0;
    }

    private bool ConfirmOperationCount(int count)
    {
        var result = WpfMessageBox.Show(this,
            $"Будет обработано {count} файл(ов). Продолжить?",
            "Подтверждение",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        return result == MessageBoxResult.OK;
    }

    private string GetImagesFolderPath()
    {
        if (Dispatcher.CheckAccess())
        {
            return GetImagesFolderPathCore();
        }

        // Allow background threads (ImageOperations) to read UI-bound textbox safely.
        return Dispatcher.Invoke(GetImagesFolderPathCore);
    }

    private string GetImagesFolderPathCore()
    {
        var path = ImagesFolderTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("Images folder is not selected.");
        }

        return path;
    }

    private async Task RunOperationAsync(string statusMessage, IReadOnlyList<string> files, Action<ImageOperationExecutionContext> action)
    {
        if (_isBusy)
        {
            WpfMessageBox.Show(this, "Wait until the current operation finishes.", "Busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (files.Count == 0)
        {
            AppendLog("No files to process.");
            return;
        }

        PrepareOperationProgress(statusMessage, files);
        SetBusy(true, statusMessage);
        AppendLog(statusMessage);

        _operationCancellation = new CancellationTokenSource();
        var context = new ImageOperationExecutionContext
        {
            CancellationToken = _operationCancellation.Token,
            ReportProgress = update => Dispatcher.Invoke(() => ApplyProgressUpdate(update))
        };

        var completedSuccessfully = false;

        try
        {
            await Task.Run(() => action(context));

            if (_operationCancellation?.IsCancellationRequested == true)
            {
                AppendLog("Operation canceled.");
                MarkPendingItemsAsCancelled();
            }
            else
            {
                AppendLog("Operation finished.");
                completedSuccessfully = true;
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Operation canceled.");
            MarkPendingItemsAsCancelled();
        }
        catch (Exception ex)
        {
            AppendLog($"Unexpected error: {ex.Message}", isError: true);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            CancelOperationButton.IsEnabled = false;
            StopEtaTimer();
            SetBusy(false);

            if (completedSuccessfully)
            {
                Dispatcher.Invoke(() =>
                {
                    WpfMessageBox.Show(this,
                        "Все файлы обработаны.",
                        "Операция завершена",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });
            }
        }
    }

    private void PrepareOperationProgress(string description, IReadOnlyList<string> files)
    {
        _fileProgressItems.Clear();
        _fileProgressLookup.Clear();

        foreach (var file in files)
        {
            var item = new FileProcessingItem(file);
            _fileProgressItems.Add(item);
            _fileProgressLookup[file] = item;
        }

        _completedFilesInOperation = 0;
        _totalFilesInOperation = files.Count;
        _operationStartTimestamp = null;
        _currentFileStartTimestamp = null;
        _lastAverageSeconds = 0;

        CurrentOperationText.Text = description;
        ProgressSummaryText.Text = _totalFilesInOperation > 0
            ? $"Обработано 0 из {_totalFilesInOperation}"
            : "Нет файлов для обработки";
        EtaText.Text = _totalFilesInOperation > 0
            ? "ETA появится после обработки первого файла."
            : string.Empty;
        OverallProgressBar.Value = 0;

        ProgressPanel.Visibility = Visibility.Visible;
        CancelOperationButton.IsEnabled = true;
        StartEtaTimer();
    }

    private void ApplyProgressUpdate(FileProcessingUpdate update)
    {
        _operationStartTimestamp ??= DateTimeOffset.Now;
        _totalFilesInOperation = Math.Max(_totalFilesInOperation, update.TotalFiles);

        var item = GetOrCreateFileItem(update.FilePath);

        switch (update.State)
        {
            case FileProcessingState.Started:
                _currentFileStartTimestamp = DateTimeOffset.Now;
                item.SetState(FileProcessingDisplayState.Processing);
                ProcessingFilesList.SelectedItem = item;
                ProcessingFilesList.ScrollIntoView(item);
                break;
            case FileProcessingState.Completed:
                _currentFileStartTimestamp = null;
                if (item.SetState(FileProcessingDisplayState.Completed))
                {
                    _completedFilesInOperation++;
                    UpdateAverageDuration();
                }
                break;
            case FileProcessingState.Skipped:
                _currentFileStartTimestamp = null;
                if (item.SetState(FileProcessingDisplayState.Skipped))
                {
                    _completedFilesInOperation++;
                    UpdateAverageDuration();
                }
                break;
            case FileProcessingState.Failed:
                _currentFileStartTimestamp = null;
                if (item.SetState(FileProcessingDisplayState.Failed, update.ErrorMessage))
                {
                    _completedFilesInOperation++;
                    UpdateAverageDuration();
                }
                break;
        }

        UpdateProgressSummary();
    }

    private void UpdateProgressSummary()
    {
        var total = Math.Max(_totalFilesInOperation, _fileProgressItems.Count);
        var completed = Math.Clamp(_completedFilesInOperation, 0, total);

        ProgressSummaryText.Text = total > 0
            ? $"Обработано {completed} из {total}"
            : "Нет файлов для обработки";

        OverallProgressBar.Value = total == 0
            ? 0
            : (double)completed / total;

        RefreshEtaText(total, completed);
    }

    private string GetEtaText(int total, int completed)
    {
        if (total == 0)
        {
            return string.Empty;
        }

        if (!_operationStartTimestamp.HasValue)
        {
            return "ETA рассчитывается...";
        }

        if (completed == 0 || _lastAverageSeconds <= 0)
        {
            return "ETA появится после обработки первого файла.";
        }

        var perFileSeconds = Math.Max(0.1, _lastAverageSeconds);
        var remainingFiles = Math.Max(0, total - completed);
        var remainingSeconds = perFileSeconds * remainingFiles;

        if (_currentFileStartTimestamp.HasValue && completed < total)
        {
            var elapsedCurrent = (DateTimeOffset.Now - _currentFileStartTimestamp.Value).TotalSeconds;
            if (elapsedCurrent > 0)
            {
                var deduction = Math.Min(elapsedCurrent, perFileSeconds);
                remainingSeconds = Math.Max(0, remainingSeconds - deduction);
            }
        }

        var eta = TimeSpan.FromSeconds(Math.Max(0, remainingSeconds));
        return $"Оставшееся время ~{eta:hh\\:mm\\:ss}";
    }

    private void UpdateAverageDuration()
    {
        if (!_operationStartTimestamp.HasValue || _completedFilesInOperation <= 0)
        {
            return;
        }

        var elapsed = (DateTimeOffset.Now - _operationStartTimestamp.Value).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        _lastAverageSeconds = Math.Max(0.1, elapsed / _completedFilesInOperation);
    }

    private void RefreshEtaText()
    {
        var total = Math.Max(_totalFilesInOperation, _fileProgressItems.Count);
        var completed = Math.Clamp(_completedFilesInOperation, 0, total);
        RefreshEtaText(total, completed);
    }

    private void RefreshEtaText(int total, int completed)
    {
        EtaText.Text = GetEtaText(total, completed);
    }

    private void StartEtaTimer()
    {
        if (!_etaUpdateTimer.IsEnabled)
        {
            _etaUpdateTimer.Start();
        }
    }

    private void StopEtaTimer()
    {
        if (_etaUpdateTimer.IsEnabled)
        {
            _etaUpdateTimer.Stop();
        }

        _currentFileStartTimestamp = null;
        _lastAverageSeconds = 0;
    }

    private FileProcessingItem GetOrCreateFileItem(string filePath)
    {
        if (_fileProgressLookup.TryGetValue(filePath, out var existing))
        {
            return existing;
        }

        var created = new FileProcessingItem(filePath);
        _fileProgressLookup[filePath] = created;
        _fileProgressItems.Add(created);
        return created;
    }

    private void MarkPendingItemsAsCancelled()
    {
        foreach (var item in _fileProgressItems)
        {
            if (!item.IsTerminal)
            {
                item.SetState(FileProcessingDisplayState.Cancelled);
            }
        }

        UpdateProgressSummary();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        ActionsPanel.IsEnabled = !busy;
        SetOperationFormsEnabled(!busy);
        ImagesFolderTextBox.IsEnabled = !busy;
        BrowseFolderButton.IsEnabled = !busy;
        StatusTextBlock.Text = status ?? (busy ? "Working..." : "Готов");
        ProgressIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetOperationFormsEnabled(bool enabled)
    {
        foreach (var view in _operationViews.Values)
        {
            view.IsEnabled = enabled;
        }
    }

    private static bool TryGetPositiveInt(WpfTextBox textBox, string fieldName, out int value)
    {
        var raw = textBox.Text?.Trim();
        if (!int.TryParse(raw, out value) || value <= 0)
        {
            WpfMessageBox.Show($"Enter a positive integer for {fieldName}.", "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning);
            textBox.Focus();
            textBox.SelectAll();
            return false;
        }

        return true;
    }

    private void NumericTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void NumericTextBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            var text = e.DataObject.GetData(DataFormats.Text) as string;
            if (!string.IsNullOrEmpty(text) && text.All(char.IsDigit))
            {
                return;
            }
        }

        e.CancelCommand();
    }

    private void AppendLog(string message, bool isError = false)
    {
        Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var prefix = isError ? "[ERR] " : string.Empty;
            LogTextBox.AppendText($"[{timestamp}] {prefix}{message}{Environment.NewLine}");
            LogTextBox.ScrollToEnd();
        });
    }

    private sealed record ConversionOption(string SourceLabel, string SourcePattern, string TargetLabel, string TargetExtension);

    private sealed class FileProcessingItem : INotifyPropertyChanged
    {
        private static readonly System.Windows.Media.Brush WaitingBrush = CreateBrush(0xF5, 0xF5, 0xF5);
        private static readonly System.Windows.Media.Brush ProcessingBrush = CreateBrush(0xFF, 0xC4, 0x1A);
        private static readonly System.Windows.Media.Brush CompletedBrush = CreateBrush(0x6F, 0xD9, 0x92);
        private static readonly System.Windows.Media.Brush SkippedBrush = CreateBrush(0xB0, 0xB4, 0xC1);
        private static readonly System.Windows.Media.Brush FailedBrush = CreateBrush(0xF4, 0x5B, 0x69);
        private static readonly System.Windows.Media.Brush CancelledBrush = CreateBrush(0xFF, 0x9F, 0x43);
        private FileProcessingDisplayState _state = FileProcessingDisplayState.Waiting;
        private string _errorMessage = string.Empty;

        public FileProcessingItem(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }
        public string FileName => Path.GetFileName(FilePath);
        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (string.Equals(_errorMessage, value, StringComparison.Ordinal))
                {
                    return;
                }

                _errorMessage = value;
                OnPropertyChanged(nameof(ErrorMessage));
            }
        }

        public string StatusText => _state switch
        {
            FileProcessingDisplayState.Waiting => "В ожидании",
            FileProcessingDisplayState.Processing => "В процессе",
            FileProcessingDisplayState.Completed => "Готово",
            FileProcessingDisplayState.Skipped => "Пропущено",
            FileProcessingDisplayState.Failed => "Ошибка",
            FileProcessingDisplayState.Cancelled => "Отменено",
            _ => string.Empty
        };

        public System.Windows.Media.Brush StatusBrush => _state switch
        {
            FileProcessingDisplayState.Waiting => WaitingBrush,
            FileProcessingDisplayState.Processing => ProcessingBrush,
            FileProcessingDisplayState.Completed => CompletedBrush,
            FileProcessingDisplayState.Skipped => SkippedBrush,
            FileProcessingDisplayState.Failed => FailedBrush,
            FileProcessingDisplayState.Cancelled => CancelledBrush,
            _ => WaitingBrush
        };

        public bool IsTerminal => _state is FileProcessingDisplayState.Completed
            or FileProcessingDisplayState.Skipped
            or FileProcessingDisplayState.Failed
            or FileProcessingDisplayState.Cancelled;

        public bool SetState(FileProcessingDisplayState newState, string? errorMessage = null)
        {
            var normalizedError = newState == FileProcessingDisplayState.Failed
                ? errorMessage ?? string.Empty
                : string.Empty;

            if (_state == newState && string.Equals(ErrorMessage, normalizedError, StringComparison.Ordinal))
            {
                return false;
            }

            _state = newState;
            ErrorMessage = normalizedError;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            return true;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static System.Windows.Media.SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    private enum FileProcessingDisplayState
    {
        Waiting,
        Processing,
        Completed,
        Skipped,
        Failed,
        Cancelled
    }

    private enum OperationPanel
    {
        None,
        Convert,
        Trim,
        Resize,
        Watermark
    }
}
