using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Jelper.Desktop.Infrastructure;

namespace Jelper.Desktop.Services;

internal sealed class ImageOperations
{
    private static readonly string[] SupportedImagePatterns = { "*.png", "*.jpg", "*.jpeg" };
    private const string RecraftDefaultUpscaleMode = "upscale16mp";
    private const string ReplicateApiTokenEnvVar = "REPLICATE_API_TOKEN";
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

    public async Task UpscaleWithRecraftAsync(IReadOnlyList<string> files, ImageOperationExecutionContext context, RecraftUpscaleOptions options)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (files.Count == 0)
        {
            _log.Info("No PNG/JPG files were found in the selected images folder.");
            return;
        }

        var pythonExecutable = options.PythonExecutablePath?.Trim();
        if (string.IsNullOrWhiteSpace(pythonExecutable))
        {
            _log.Error("Python executable path was not provided.");
            return;
        }

        var scriptPath = options.ScriptPath?.Trim();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            _log.Error("Python CLI script was not found. Recraft Upscale cannot run.");
            return;
        }

        var token = options.ApiToken?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.Error($"Environment variable {ReplicateApiTokenEnvVar} is not set. Recraft Upscale cannot run.");
            return;
        }

        var pythonInfo = !string.IsNullOrWhiteSpace(options.PythonVersionDescription)
            ? options.PythonVersionDescription!
            : pythonExecutable;
        _log.Info($"Python runtime: {pythonInfo}");
        _log.Info($"Python CLI script: {scriptPath}");

        var total = files.Count;
        _log.Info($"Found {total} PNG/JPG file(s). Upscaling via Python CLI...");

        for (var index = 0; index < total; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var progress = FormatProgress(index + 1, total);
            var fileName = Path.GetFileName(file);
            var outputPath = GetPythonOutputPath(file);

            ReportProgress(context, file, FileProcessingState.Started, total);

            try
            {
                var result = await RunPythonUpscaleAsync(file, outputPath, token, options, context.CancellationToken);
                var producedPath = result.OutputPath;

                if (!File.Exists(producedPath))
                {
                    throw new InvalidOperationException("Python CLI did not produce an output file.");
                }

                if (!producedPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(producedPath, outputPath, true);
                    producedPath = outputPath;
                }

                TryDeleteOriginal(file, producedPath);

                if (!string.IsNullOrWhiteSpace(result.RemoteUrl))
                {
                    _log.Info($"{progress} CDN URL: {result.RemoteUrl}");
                }

                var extra = string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" ({result.Message})";
                _log.Info($"{progress} Updated via Python CLI{extra}. Новый файл: {producedPath}");
                ReportProgress(context, file, FileProcessingState.Completed, total);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error($"{progress} Failed to upscale {fileName}: {ex.Message}");
                ReportProgress(context, file, FileProcessingState.Failed, total, ex.Message);
            }
        }

        _log.Info("Recraft Upscale finished for all PNG/JPG files.");
    }

    private async Task<PythonCliResult> RunPythonUpscaleAsync(string inputPath, string outputPath, string token, RecraftUpscaleOptions options, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.PythonExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(options.ScriptPath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add(options.ScriptPath);
        startInfo.ArgumentList.Add("--image");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);

        var mode = string.IsNullOrWhiteSpace(options.UpscaleMode) ? RecraftDefaultUpscaleMode : options.UpscaleMode!;
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(mode);

        startInfo.Environment[ReplicateApiTokenEnvVar] = token;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Python CLI process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"Python CLI exited with code {process.ExitCode}: {message?.Trim()}");
        }

        var payload = stdout?.Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException("Python CLI did not return any data.");
        }

        return ParsePythonCliResult(payload);
    }

    private static PythonCliResult ParsePythonCliResult(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var status = TryGetString(root, "status") ?? "ok";
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var error = TryGetString(root, "error") ?? TryGetString(root, "message") ?? status;
                throw new InvalidOperationException($"Python CLI error: {error}");
            }

            var outputPath = TryGetString(root, "output_path");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new InvalidOperationException("Python CLI response did not include output_path.");
            }

            var remoteUrl = TryGetString(root, "remote_url");
            var message = TryGetString(root, "message");
            return new PythonCliResult(outputPath, remoteUrl, message);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON from Python CLI: {ex.Message}");
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static string GetPythonOutputPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, fileName + ".webp");
    }

    private static void TryDeleteOriginal(string originalPath, string producedPath)
    {
        if (originalPath.Equals(producedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (File.Exists(originalPath))
            {
                File.Delete(originalPath);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    private static string GetMimeTypeForPath(string path)
    {
        var extension = Path.GetExtension(path)?.ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
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

    private sealed record PythonCliResult(string OutputPath, string? RemoteUrl, string? Message);
}

internal sealed class RecraftUpscaleOptions
{
    public required string PythonExecutablePath { get; init; }
    public required string ScriptPath { get; init; }
    public required string ApiToken { get; init; }
    public string? PythonVersionDescription { get; init; }
    public string? UpscaleMode { get; init; } = null;
}
