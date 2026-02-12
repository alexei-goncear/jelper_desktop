using System;
using System.Collections.Generic;
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
    private const string RecraftDefaultUpscaleMode = "upscale16mp";
    private const string RecraftApiTokenEnvVar = "RECRAFT_API_TOKEN";
    private const string LegacyReplicateApiTokenEnvVar = "REPLICATE_API_TOKEN";
    private static readonly Uri RecraftApiBaseUri = new("https://external.api.recraft.ai/");
    private const string RecraftCrispUpscalePath = "v1/images/crispUpscale";
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

    public async Task UpscaleWithRecraftAsync(IReadOnlyList<string> files, ImageOperationExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(context);

        if (files.Count == 0)
        {
            _log.Info("No PNG/JPG files were found in the selected images folder.");
            return;
        }

        var token = Environment.GetEnvironmentVariable(RecraftApiTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable(LegacyReplicateApiTokenEnvVar);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            _log.Error($"Environment variable {RecraftApiTokenEnvVar} is not set. Recraft Upscale cannot run.");
            return;
        }

        using var httpClient = CreateRecraftClient(token.Trim());
        var total = files.Count;
        _log.Info($"Found {total} PNG/JPG file(s). Sending to Recraft Crisp Upscale...");

        for (var index = 0; index < total; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var progress = FormatProgress(index + 1, total);
            var fileName = Path.GetFileName(file);

            ReportProgress(context, file, FileProcessingState.Started, total);

            try
            {
                var result = await SendRecraftUpscaleRequestAsync(httpClient, file, context.CancellationToken);

                if (!string.IsNullOrWhiteSpace(result.ImageUrl))
                {
                    await DownloadResultAsync(httpClient, result.ImageUrl, file, context.CancellationToken);
                }
                else if (!string.IsNullOrWhiteSpace(result.Base64Data))
                {
                    await WriteBase64ImageAsync(result.Base64Data, file, context.CancellationToken);
                }
                else
                {
                    throw new InvalidOperationException("Recraft response did not include image data.");
                }

                _log.Info($"{progress} Updated {fileName} via Recraft Crisp Upscale.");
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

    private static HttpClient CreateRecraftClient(string token)
    {
        var client = new HttpClient
        {
            BaseAddress = RecraftApiBaseUri,
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JelperDesktop/1.0");
        return client;
    }

    private async Task<RecraftProcessImageResult> SendRecraftUpscaleRequestAsync(HttpClient httpClient, string filePath, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var fileInfo = new FileInfo(filePath);
        var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeTypeForPath(filePath));
        fileContent.Headers.ContentLength = Math.Max(0, fileInfo.Exists ? fileInfo.Length : fileStream.Length);
        form.Add(fileContent, "image", Path.GetFileName(filePath));
        form.Add(new StringContent("url"), "response_format");
        form.Add(new StringContent(RecraftDefaultUpscaleMode), "upscale");

        using var response = await httpClient.PostAsync(RecraftCrispUpscalePath, form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "request Recraft crisp upscale");
        return ParseRecraftProcessImageResponse(body);
    }

    private static async Task DownloadResultAsync(HttpClient httpClient, string downloadUrl, string destinationPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var tempPath = Path.GetTempFileName();
        await using (var outputStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await responseStream.CopyToAsync(outputStream, cancellationToken);
        }

        File.Move(tempPath, destinationPath, true);
    }

    private static async Task WriteBase64ImageAsync(string base64Data, string destinationPath, CancellationToken cancellationToken)
    {
        var bytes = Convert.FromBase64String(base64Data);
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await output.WriteAsync(bytes, cancellationToken);
    }

    private static RecraftProcessImageResult ParseRecraftProcessImageResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Recraft response body was empty.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("image", out var imageElement) || imageElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Recraft response did not include image metadata.");
        }

        var url = TryGetString(imageElement, "url");
        var base64 = TryGetString(imageElement, "b64_json");
        return new RecraftProcessImageResult(url, base64);
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

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var errorMessage = TryExtractErrorMessage(body);
        var statusText = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            statusText += $": {errorMessage}";
        }

        throw new InvalidOperationException($"Unable to {operation} via Recraft: {statusText}.");
    }

    private static string? TryExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return TryGetString(root, "error") ?? TryGetString(root, "detail");
        }
        catch (JsonException)
        {
            return null;
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

    private sealed record RecraftProcessImageResult(string? ImageUrl, string? Base64Data);
}
