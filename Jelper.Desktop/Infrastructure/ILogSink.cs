namespace Jelper.Desktop.Infrastructure;

internal interface ILogSink
{
    void Info(string message);
    void Error(string message);
}
