using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageMagick;
using Jelper.Desktop.Infrastructure;

namespace Jelper.Desktop.Services;

internal sealed class ImageOperations
{
    private static readonly string[] SupportedImagePatterns = { "*.png", "*.jpg", "*.jpeg" };
    private readonly ILogSink _log;
    private readonly Func<string> _imagesDirectoryProvider;

    public ImageOperations(ILogSink log, Func<string> imagesDirectoryProvider)
    {
        _log = log;
        _imagesDirectoryProvider = imagesDirectoryProvider;
    }

    public int GetSupportedImageFileCount() => GetSupportedImageFiles().Count;

    public IReadOnlyList<string> ListSupportedImageFiles() => GetSupportedImageFiles();

    public IReadOnlyList<string> ListFilesByPattern(string searchPattern)
    {
        if (string.IsNullOrWhiteSpace(searchPattern))
        {
            return Array.Empty<string>();
        }

        return GetFiles(searchPattern);
    }

    public bool HasSupportedImageFiles() => GetSupportedImageFileCount() > 0;

    public void ConvertFiles(IReadOnlyList<string> files, string targetExtension, ImageOperationExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(targetExtension))
        {
            throw new ArgumentException("Target extension is required.", nameof(targetExtension));
        }

        if (files.Count == 0)
        {
            _log.Info("No files were found for conversion in the selected images folder.");
            return;
        }

        var converted = 0;
        var total = files.Count;
        var targetExtensionDisplay = targetExtension.TrimStart('.').ToUpperInvariant();
        _log.Info($"Found {total} file(s). Converting to {targetExtensionDisplay}...");

        for (var index = 0; index < total; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var destinationPath = Path.ChangeExtension(file, targetExtension);
            var progress = FormatProgress(index + 1, total);
            var fileName = Path.GetFileName(file);

            ReportProgress(context, file, FileProcessingState.Started, total);

            try
            {
                var existed = File.Exists(destinationPath);
                using var image = new MagickImage(file);
                var format = GetFormatForPath(destinationPath);
                image.Write(destinationPath, format);
                converted++;

                if (!file.Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteSource(file, progress);
                }

                var action = existed ? "Updated" : "Created";
                _log.Info($"{progress} {action} {Path.GetFileName(destinationPath)} from {fileName}.");
                ReportProgress(context, file, FileProcessingState.Completed, total);
            }
            catch (Exception ex)
            {
                _log.Error($"{progress} Failed to convert {fileName}: {ex.Message}");
                ReportProgress(context, file, FileProcessingState.Failed, total, ex.Message);
            }
        }

        _log.Info($"Conversion finished. Converted {converted} of {total}.");
    }

    public void RemoveWatermark(int pixelsToRemove, IReadOnlyList<string> files, ImageOperationExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(context);

        if (files.Count == 0)
        {
            _log.Info("No PNG/JPG files were found in the selected images folder.");
            return;
        }

        var total = files.Count;
        _log.Info($"Found {total} PNG/JPG file(s). Removing watermark...");

        for (var index = 0; index < total; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var progress = FormatProgress(index + 1, total);
            var fileName = Path.GetFileName(file);

            ReportProgress(context, file, FileProcessingState.Started, total);

            try
            {
                using var image = new MagickImage(file);
                if (image.Height <= pixelsToRemove)
                {
                    _log.Error($"{progress} Cannot trim {pixelsToRemove}px from {fileName} because the image height is {image.Height}px. Skipped.");
                    ReportProgress(context, file, FileProcessingState.Skipped, total);
                    continue;
                }

                var originalWidth = image.Width;
                var originalHeight = image.Height;
                var targetHeight = originalHeight - pixelsToRemove;
                var targetWidth = (int)Math.Round(originalWidth * targetHeight / (double)originalHeight);
                targetWidth = Math.Clamp(targetWidth, 1, originalWidth);
                var widthReduction = originalWidth - targetWidth;
                var leftCrop = widthReduction / 2;

                var geometry = new MagickGeometry(leftCrop, 0, targetWidth, targetHeight)
                {
                    IgnoreAspectRatio = false
                };

                image.Crop(geometry);
                var format = GetFormatForPath(file);
                image.Write(file, format);
                _log.Info($"{progress} Updated {fileName} (trimmed {pixelsToRemove}px).");
                ReportProgress(context, file, FileProcessingState.Completed, total);
            }
            catch (Exception ex)
            {
                _log.Error($"{progress} Failed to update {fileName}: {ex.Message}");
                ReportProgress(context, file, FileProcessingState.Failed, total, ex.Message);
            }
        }

        _log.Info("Watermark removal finished for all PNG/JPG files.");
    }

    public void ResizeImages(int targetWidth, int targetHeight, IReadOnlyList<string> files, ImageOperationExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(context);

        if (files.Count == 0)
        {
            _log.Info("No PNG/JPG files were found in the selected images folder.");
            return;
        }

        var total = files.Count;
        _log.Info($"Found {total} PNG/JPG file(s). Resizing...");

        for (var index = 0; index < total; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var progress = FormatProgress(index + 1, total);
            var fileName = Path.GetFileName(file);

            ReportProgress(context, file, FileProcessingState.Started, total);

            try
            {
                using var image = new MagickImage(file);
                var geometry = new MagickGeometry(targetWidth, targetHeight)
                {
                    IgnoreAspectRatio = true
                };
                image.Resize(geometry);
                var format = GetFormatForPath(file);
                image.Write(file, format);
                _log.Info($"{progress} Updated {fileName} ({targetWidth}x{targetHeight}).");
                ReportProgress(context, file, FileProcessingState.Completed, total);
            }
            catch (Exception ex)
            {
                _log.Error($"{progress} Failed to resize {fileName}: {ex.Message}");
                ReportProgress(context, file, FileProcessingState.Failed, total, ex.Message);
            }
        }

        _log.Info("Resize finished for all PNG/JPG files.");
    }

    public void ResizeAndWatermark(int targetWidth, int targetHeight, string watermarkPath, IReadOnlyList<string> files, ImageOperationExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(context);

        if (!File.Exists(watermarkPath))
        {
            _log.Info($"Watermark file {Path.GetFileName(watermarkPath)} was not found. Nothing to do.");
            return;
        }

        if (files.Count == 0)
        {
            _log.Info("No PNG/JPG files were found in the selected images folder.");
            return;
        }

        var watermarkFileName = Path.GetFileName(watermarkPath);
        _log.Info($"Found {files.Count} PNG/JPG file(s). Preparing watermark {watermarkFileName}...");

        context.CancellationToken.ThrowIfCancellationRequested();

        MagickImage watermark;
        try
        {
            watermark = new MagickImage(watermarkPath);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load watermark {watermarkFileName}: {ex.Message}");
            return;
        }

        using (watermark)
        {
            var total = files.Count;
            for (var index = 0; index < total; index++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var file = files[index];
                var progress = FormatProgress(index + 1, total);
                var fileName = Path.GetFileName(file);

                ReportProgress(context, file, FileProcessingState.Started, total);

                try
                {
                    using var image = new MagickImage(file);
                    var geometry = new MagickGeometry(targetWidth, targetHeight)
                    {
                        IgnoreAspectRatio = true
                    };
                    image.Resize(geometry);

                    using var preparedWatermark = PrepareWatermarkFor(image, watermark);
                    var offsetX = Math.Max(0, image.Width - preparedWatermark.Width);
                    var offsetY = Math.Max(0, image.Height - preparedWatermark.Height);
                    image.Composite(preparedWatermark, offsetX, offsetY, CompositeOperator.Over);

                    var format = GetFormatForPath(file);
                    image.Write(file, format);
                    _log.Info($"{progress} Updated {fileName} ({targetWidth}x{targetHeight}, watermark: {watermarkFileName}).");
                    ReportProgress(context, file, FileProcessingState.Completed, total);
                }
                catch (Exception ex)
                {
                    _log.Error($"{progress} Failed to watermark {fileName}: {ex.Message}");
                    ReportProgress(context, file, FileProcessingState.Failed, total, ex.Message);
                }
            }

            _log.Info("Resize + watermark finished for all PNG/JPG files.");
        }
    }

    private IMagickImage<ushort> PrepareWatermarkFor(IMagickImage<ushort> baseImage, IMagickImage<ushort> watermark)
    {
        var clone = watermark.Clone();
        if (clone.Width <= baseImage.Width && clone.Height <= baseImage.Height)
        {
            return clone;
        }

        var geometry = new MagickGeometry(baseImage.Width, baseImage.Height)
        {
            IgnoreAspectRatio = false
        };
        clone.Resize(geometry);
        return clone;
    }

    private List<string> GetFiles(string searchPattern)
    {
        var imagesDirectory = EnsureImagesDirectory();
        return Directory.EnumerateFiles(imagesDirectory, searchPattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> GetSupportedImageFiles()
    {
        var imagesDirectory = EnsureImagesDirectory();
        return SupportedImagePatterns
            .SelectMany(pattern => Directory.EnumerateFiles(imagesDirectory, pattern, SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string EnsureImagesDirectory()
    {
        var directory = _imagesDirectoryProvider()
            ?? throw new InvalidOperationException("Images folder path is not set.");

        directory = directory.Trim();
        if (directory.Length == 0)
        {
            throw new InvalidOperationException("Images folder path is not set.");
        }

        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static MagickFormat GetFormatForPath(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return MagickFormat.Png;
        }

        return extension.ToLowerInvariant() switch
        {
            ".png" => MagickFormat.Png,
            ".jpg" => MagickFormat.Jpeg,
            ".jpeg" => MagickFormat.Jpeg,
            _ => MagickFormat.Png
        };
    }

    private static string FormatProgress(int current, int total) => $"[{current}/{total}]";

    private static void ReportProgress(ImageOperationExecutionContext context, string file, FileProcessingState state, int total, string? errorMessage = null)
    {
        context.ReportProgress(new FileProcessingUpdate(file, state, total, errorMessage));
    }

    private void TryDeleteSource(string sourcePath, string progress)
    {
        try
        {
            File.Delete(sourcePath);
        }
        catch (Exception ex)
        {
            _log.Error($"{progress} Converted but failed to delete original {Path.GetFileName(sourcePath)}: {ex.Message}");
        }
    }
}
