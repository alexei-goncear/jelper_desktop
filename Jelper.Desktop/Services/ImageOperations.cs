using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Jelper.Desktop.Infrastructure;

namespace Jelper.Desktop.Services;

internal sealed class ImageOperations
{
    private static readonly string[] SupportedImagePatterns = { "*.png", "*.jpg", "*.jpeg" };
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private const string RecraftDefaultUpscaleMode = "upscale16mp";
    private const string ReplicateApiTokenEnvVar = "REPLICATE_API_TOKEN";
    private const string OpenAiApiKeyEnvVar = "OPENAI_API_KEY";
    private const string LightXUploadUrlApi = "https://api.lightxeditor.com/external/api/v2/uploadImageUrl";
    private const string LightXCleanupApi = "https://api.lightxeditor.com/external/api/v2/cleanup-picture";
    private const string LightXOrderStatusApi = "https://api.lightxeditor.com/external/api/v2/order-status";
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

    public async Task EditWithGptAsync(IReadOnlyList<string> files, ImageOperationExecutionContext context, GptImageEditOptions options)
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
            _log.Error("Python CLI script was not found. GPT image edit cannot run.");
            return;
        }

        var apiKey = options.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.Error($"Environment variable {OpenAiApiKeyEnvVar} is not set. GPT image edit cannot run.");
            return;
        }

        var prompt = options.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            _log.Error("Prompt was not provided. GPT image edit cannot run.");
            return;
        }

        var pythonInfo = !string.IsNullOrWhiteSpace(options.PythonVersionDescription)
            ? options.PythonVersionDescription!
            : pythonExecutable;
        _log.Info($"Python runtime: {pythonInfo}");
        _log.Info($"Python CLI script: {scriptPath}");

        var total = files.Count;
        _log.Info($"Found {total} PNG/JPG file(s). Editing via GPT image API...");

        for (var index = 0; index < total; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var progress = FormatProgress(index + 1, total);
            var fileName = Path.GetFileName(file);
            var outputPath = GetGptOutputPath(file);

            ReportProgress(context, file, FileProcessingState.Started, total);

            try
            {
                var result = await RunPythonGptEditAsync(file, outputPath, options, context.CancellationToken);
                var producedPath = result.OutputPath;

                if (!File.Exists(producedPath))
                {
                    throw new InvalidOperationException("Python CLI did not produce an output file.");
                }

                var finalPath = GetGptFinalPath(file);
                if (!producedPath.Equals(finalPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(producedPath, finalPath, true);
                    producedPath = finalPath;
                }

                var extra = string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" ({result.Message})";
                _log.Info($"{progress} Updated via GPT image API{extra}. New file: {producedPath}");
                ReportProgress(context, file, FileProcessingState.Completed, total);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error($"{progress} Failed to edit {fileName}: {ex.Message}");
                ReportProgress(context, file, FileProcessingState.Failed, total, ex.Message);
            }
        }

        _log.Info("GPT image edit finished for all PNG/JPG files.");
    }

    public async Task CleanupWithLightXAsync(IReadOnlyList<string> files, ImageOperationExecutionContext context, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (files.Count == 0)
        {
            _log.Info("No PNG/JPG files were found in the selected images folder.");
            return;
        }

        var total = files.Count;
        _log.Info($"Found {total} PNG/JPG file(s). Cleaning via LightX AI Cleanup...");

        for (var index = 0; index < total; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var progress = FormatProgress(index + 1, total);
            var fileName = Path.GetFileName(file);
            var outputPath = GetLightXOutputPath(file);

            ReportProgress(context, file, FileProcessingState.Started, total);

            try
            {
                var result = await RunLightXCleanupAsync(file, outputPath, apiKey, context.CancellationToken);
                var producedPath = result.OutputPath;

                if (!File.Exists(producedPath))
                {
                    throw new InvalidOperationException("LightX did not produce an output file.");
                }

                var finalPath = GetLightXFinalPath(file);
                if (!producedPath.Equals(finalPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(producedPath, finalPath, true);
                    producedPath = finalPath;
                }

                TryDeleteOriginal(file, producedPath);

                if (!string.IsNullOrWhiteSpace(result.RemoteUrl))
                {
                    _log.Info($"{progress} CDN URL: {result.RemoteUrl}");
                }

                var extra = string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" ({result.Message})";
                _log.Info($"{progress} Cleaned via LightX AI Cleanup{extra}. New file: {producedPath}");
                ReportProgress(context, file, FileProcessingState.Completed, total);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error($"{progress} Failed to clean {fileName}: {ex.Message}");
                ReportProgress(context, file, FileProcessingState.Failed, total, ex.Message);
            }
        }

        _log.Info("LightX AI Cleanup finished for all PNG/JPG files.");
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

    private async Task<PythonCliResult> RunPythonGptEditAsync(string inputPath, string outputPath, GptImageEditOptions options, CancellationToken cancellationToken)
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
        startInfo.ArgumentList.Add("--prompt");
        startInfo.ArgumentList.Add(options.Prompt);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(options.Model);

        startInfo.Environment[OpenAiApiKeyEnvVar] = options.ApiKey;

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

    private async Task<PythonCliResult> RunLightXCleanupAsync(string inputPath, string outputPath, string apiKey, CancellationToken cancellationToken)
    {
        string? maskPath = null;

        try
        {
            maskPath = CreateLightXMask(inputPath);

            var imageUpload = await RequestLightXUploadAsync(inputPath, apiKey, cancellationToken);
            await UploadFileToLightXAsync(imageUpload.UploadImageUrl, inputPath, cancellationToken);

            var maskUpload = await RequestLightXUploadAsync(maskPath, apiKey, cancellationToken);
            await UploadFileToLightXAsync(maskUpload.UploadImageUrl, maskPath, cancellationToken);

            var cleanupOrder = await StartLightXCleanupAsync(imageUpload.ImageUrl, maskUpload.ImageUrl, apiKey, cancellationToken);
            var cleanupResult = await PollLightXOrderAsync(cleanupOrder.OrderId, cleanupOrder.MaxRetriesAllowed, apiKey, cancellationToken);

            await DownloadFileAsync(cleanupResult.OutputUrl, outputPath, cancellationToken);

            return new PythonCliResult(
                outputPath,
                cleanupResult.OutputUrl,
                $"order_id={cleanupOrder.OrderId}, status={cleanupResult.Status}, retries={cleanupOrder.MaxRetriesAllowed}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"LightX cleanup failed: {ex.Message}", ex);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(maskPath))
            {
                TryDeleteFile(maskPath);
            }
        }
    }

    private async Task<LightXUploadTicket> RequestLightXUploadAsync(string filePath, string apiKey, CancellationToken cancellationToken)
    {
        var payload = new
        {
            uploadType = "imageUrl",
            size = new FileInfo(filePath).Length,
            contentType = GetMimeTypeForPath(filePath)
        };

        using var request = CreateLightXJsonRequest(HttpMethod.Post, LightXUploadUrlApi, apiKey, payload);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        using var root = await ReadLightXJsonAsync(response, cancellationToken);
        var body = GetLightXBody(root.RootElement);

        var uploadImageUrl = GetRequiredString(body, "uploadImage");
        var imageUrl = GetRequiredString(body, "imageUrl");
        return new LightXUploadTicket(uploadImageUrl, imageUrl);
    }

    private async Task UploadFileToLightXAsync(string uploadUrl, string filePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(GetMimeTypeForPath(filePath));

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "LightX upload failed");
    }

    private async Task<LightXCleanupOrder> StartLightXCleanupAsync(string imageUrl, string maskImageUrl, string apiKey, CancellationToken cancellationToken)
    {
        var payload = new
        {
            imageUrl,
            maskedImageUrl = maskImageUrl
        };

        using var request = CreateLightXJsonRequest(HttpMethod.Post, LightXCleanupApi, apiKey, payload);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        using var root = await ReadLightXJsonAsync(response, cancellationToken);
        var body = GetLightXBody(root.RootElement);

        var orderId = GetRequiredString(body, "orderId");
        var maxRetriesAllowed = TryGetInt32(body, "maxRetriesAllowed") ?? 5;
        return new LightXCleanupOrder(orderId, Math.Max(1, maxRetriesAllowed));
    }

    private async Task<LightXCleanupResult> PollLightXOrderAsync(string orderId, int maxRetriesAllowed, string apiKey, CancellationToken cancellationToken)
    {
        var lastStatus = "init";

        for (var attempt = 0; attempt < maxRetriesAllowed; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }

            using var request = CreateLightXJsonRequest(HttpMethod.Post, LightXOrderStatusApi, apiKey, new { orderId });
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            using var root = await ReadLightXJsonAsync(response, cancellationToken);
            var body = GetLightXBody(root.RootElement);

            var status = TryGetString(body, "status");
            if (!string.IsNullOrWhiteSpace(status))
            {
                lastStatus = status;
            }

            if (string.Equals(lastStatus, "active", StringComparison.OrdinalIgnoreCase))
            {
                var outputUrl = GetRequiredString(body, "output");
                return new LightXCleanupResult(lastStatus, outputUrl);
            }

            if (string.Equals(lastStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("LightX AI Cleanup marked the order as failed.");
            }
        }

        throw new InvalidOperationException($"LightX AI Cleanup did not finish in time. Last status: {lastStatus}.");
    }

    private async Task DownloadFileAsync(string url, string outputPath, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "Failed to download LightX output");

        await using var inputStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await inputStream.CopyToAsync(outputStream, cancellationToken);
    }

    private static string CreateLightXMask(string inputPath)
    {
        using var source = new MagickImage(inputPath);

        using var mask = new MagickImage(MagickColors.Black, source.Width, source.Height);
        mask.ColorType = ColorType.Grayscale;

        const double percent = 0.0833;

        var rectWidth = Math.Max(1, (int)Math.Round(source.Width * percent));
        var rectHeight = Math.Max(1, (int)Math.Round(source.Height * percent));

        var x = source.Width - rectWidth;
        var y = source.Height - rectHeight;

        using var pixels = mask.GetPixels();

        var white = new ushort[] { Quantum.Max };

        for (var py = y; py < y + rectHeight && py < source.Height; py++)
        {
            for (var px = x; px < x + rectWidth && px < source.Width; px++)
            {
                pixels.SetPixel(px, py, white);
            }
        }

        var maskPath = Path.Combine(Path.GetTempPath(), $"jelper-mask-{Guid.NewGuid():N}.png");
        mask.Write(maskPath, MagickFormat.Png);

        return maskPath;
    }

    private static HttpRequestMessage CreateLightXJsonRequest(HttpMethod method, string url, string apiKey, object payload)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    private static async Task<JsonDocument> ReadLightXJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}: {content}");
        }

        try
        {
            return JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"LightX returned invalid JSON: {ex.Message}");
        }
    }

    private static JsonElement GetLightXBody(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
        {
            var message = TryGetString(root, "message") ?? "LightX response did not include body.";
            throw new InvalidOperationException(message);
        }

        return body;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = TryGetString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"LightX response did not include {propertyName}.");
        }

        return value;
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        return null;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken, string prefix)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"{prefix}: {(int)response.StatusCode} {response.ReasonPhrase}. {content}");
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

    private static string GetGptOutputPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        return Path.Combine(directory, fileName + ".gpt-temp" + extension);
    }

    private static string GetGptFinalPath(string sourcePath) => sourcePath;

    private static string GetLightXOutputPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, fileName + ".lightx-temp.jpg");
    }

    private static string GetLightXFinalPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(directory, fileName + ".jpg");
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore temp cleanup errors.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(100)
        };
        return client;
    }

    private sealed record PythonCliResult(string OutputPath, string? RemoteUrl, string? Message);
    private sealed record LightXUploadTicket(string UploadImageUrl, string ImageUrl);
    private sealed record LightXCleanupOrder(string OrderId, int MaxRetriesAllowed);
    private sealed record LightXCleanupResult(string Status, string OutputUrl);
}

internal sealed class RecraftUpscaleOptions
{
    public required string PythonExecutablePath { get; init; }
    public required string ScriptPath { get; init; }
    public required string ApiToken { get; init; }
    public string? PythonVersionDescription { get; init; }
    public string? UpscaleMode { get; init; } = null;
}

internal sealed class GptImageEditOptions
{
    public required string PythonExecutablePath { get; init; }
    public required string ScriptPath { get; init; }
    public required string ApiKey { get; init; }
    public required string Prompt { get; init; }
    public string? PythonVersionDescription { get; init; }
    public string Model { get; init; } = "gpt-image-1";
}
