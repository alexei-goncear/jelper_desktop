using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
    private readonly Dictionary<OperationPanel, UIElement> _operationViews;
    private OperationPanel _activePanel = OperationPanel.None;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        _operationViews = new Dictionary<OperationPanel, UIElement>
        {
            { OperationPanel.Convert, ConvertForm },
            { OperationPanel.Trim, TrimForm },
            { OperationPanel.Resize, ResizeForm },
            { OperationPanel.Watermark, WatermarkForm }
        };

        _operations = new ImageOperations(this, GetImagesFolderPath);

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
            !TryGetFileCount(OperationPanel.Convert, out var count) ||
            !ConfirmOperationCount(count))
        {
            return;
        }

        await RunOperationAsync("Converting WEBP files...", () => _operations.ConvertWebpToPng());
    }

    private async void TrimButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureImagesFolderSelected() ||
            !TryGetFileCount(OperationPanel.Trim, out var count) ||
            !ConfirmOperationCount(count))
        {
            return;
        }

        if (!TryGetPositiveInt(PixelsToTrimTextBox, "Pixels to trim", out var pixels))
        {
            return;
        }

        await RunOperationAsync($"Removing {pixels}px watermark strip...", () => _operations.RemoveWatermark(pixels));
    }

    private async void ResizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureImagesFolderSelected() ||
            !TryGetFileCount(OperationPanel.Resize, out var count) ||
            !ConfirmOperationCount(count))
        {
            return;
        }

        if (!TryGetPositiveInt(ResizeWidthTextBox, "Width", out var width) ||
            !TryGetPositiveInt(ResizeHeightTextBox, "Height", out var height))
        {
            return;
        }

        await RunOperationAsync($"Resizing images to {width}x{height}...", () => _operations.ResizeImages(width, height));
    }

    private async void WatermarkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureImagesFolderSelected() ||
            !TryGetFileCount(OperationPanel.Watermark, out var count) ||
            !ConfirmOperationCount(count))
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
            () => _operations.ResizeAndWatermark(width, height, watermarkPath));
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
        OperationPanel.Convert => "WEBP → PNG",
        OperationPanel.Trim => "Удаление полосы",
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

    private bool TryGetFileCount(OperationPanel panel, out int count)
    {
        count = panel switch
        {
            OperationPanel.Convert => _operations.GetWebpFileCount(),
            _ => _operations.GetSupportedImageFileCount()
        };

        if (count > 0)
        {
            return true;
        }

        var message = panel switch
        {
            OperationPanel.Convert => "В выбранной папке нет WEBP файлов.",
            _ => "В выбранной папке нет PNG/JPG файлов для обработки."
        };

        WpfMessageBox.Show(this, message, "Нет подходящих файлов", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
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

    private async Task RunOperationAsync(string statusMessage, Action action)
    {
        if (_isBusy)
        {
            WpfMessageBox.Show(this, "Wait until the current operation finishes.", "Busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, statusMessage);
        AppendLog(statusMessage);

        try
        {
            await Task.Run(action);
            AppendLog("Operation finished.");
        }
        catch (Exception ex)
        {
            AppendLog($"Unexpected error: {ex.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        ActionsPanel.IsEnabled = !busy;
        DetailsCard.IsEnabled = !busy;
        ImagesFolderTextBox.IsEnabled = !busy;
        BrowseFolderButton.IsEnabled = !busy;
        StatusTextBlock.Text = status ?? (busy ? "Working..." : "Готов");
        ProgressIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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

    private enum OperationPanel
    {
        None,
        Convert,
        Trim,
        Resize,
        Watermark
    }
}
