using System;
using System.IO;

namespace Jelper.Desktop.Infrastructure;

internal static class UserSettings
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JelperDesktop");

    private static readonly string ImagesPathFile = Path.Combine(SettingsFolder, "images-path.txt");

    public static string? LoadImagesFolderPath()
    {
        try
        {
            if (!File.Exists(ImagesPathFile))
            {
                return null;
            }

            var content = File.ReadAllText(ImagesPathFile).Trim();
            return string.IsNullOrEmpty(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveImagesFolderPath(string? path)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);

            if (string.IsNullOrWhiteSpace(path))
            {
                File.WriteAllText(ImagesPathFile, string.Empty);
            }
            else
            {
                File.WriteAllText(ImagesPathFile, path);
            }
        }
        catch
        {
            // Ignore persistence errors, they should not block the UI.
        }
    }
}
