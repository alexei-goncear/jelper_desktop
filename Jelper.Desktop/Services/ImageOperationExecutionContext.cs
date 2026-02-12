using System;
using System.Threading;

namespace Jelper.Desktop.Services;

internal sealed class ImageOperationExecutionContext
{
    public required Action<FileProcessingUpdate> ReportProgress { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

internal readonly record struct FileProcessingUpdate(
    string FilePath,
    FileProcessingState State,
    int TotalFiles,
    string? ErrorMessage = null);

internal enum FileProcessingState
{
    Started,
    Completed,
    Skipped,
    Failed
}
