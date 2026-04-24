using System;
using System.IO;

namespace Jelper.Desktop.Infrastructure;

internal static class UserSettings
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JelperDesktop");

    private static readonly string ImagesPathFile = Path.Combine(SettingsFolder, "images-path.txt");
    private static readonly string ReplicateTokenFile = Path.Combine(SettingsFolder, "replicate-token.txt");
    private static readonly string OpenAiApiKeyFile = Path.Combine(SettingsFolder, "openai-api-key.txt");
    private static readonly string LightXApiKeyFile = Path.Combine(SettingsFolder, "lightx-api-key.txt");
    private static readonly string GptPromptFile = Path.Combine(SettingsFolder, "gpt-prompt.txt");

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

    public static string? LoadReplicateToken()
    {
        try
        {
            if (!File.Exists(ReplicateTokenFile))
            {
                return null;
            }

            var content = File.ReadAllText(ReplicateTokenFile).Trim();
            return string.IsNullOrEmpty(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveReplicateToken(string? token)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);

            if (string.IsNullOrWhiteSpace(token))
            {
                File.WriteAllText(ReplicateTokenFile, string.Empty);
            }
            else
            {
                File.WriteAllText(ReplicateTokenFile, token);
            }
        }
        catch
        {
            // Ignore persistence errors.
        }
    }

    public static string? LoadOpenAiApiKey()
    {
        try
        {
            if (!File.Exists(OpenAiApiKeyFile))
            {
                return null;
            }

            var content = File.ReadAllText(OpenAiApiKeyFile).Trim();
            return string.IsNullOrEmpty(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveOpenAiApiKey(string? apiKey)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                File.WriteAllText(OpenAiApiKeyFile, string.Empty);
            }
            else
            {
                File.WriteAllText(OpenAiApiKeyFile, apiKey);
            }
        }
        catch
        {
            // Ignore persistence errors.
        }
    }

    public static string? LoadLightXApiKey()
    {
        try
        {
            if (!File.Exists(LightXApiKeyFile))
            {
                return null;
            }

            var content = File.ReadAllText(LightXApiKeyFile).Trim();
            return string.IsNullOrEmpty(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveLightXApiKey(string? apiKey)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                File.WriteAllText(LightXApiKeyFile, string.Empty);
            }
            else
            {
                File.WriteAllText(LightXApiKeyFile, apiKey);
            }
        }
        catch
        {
            // Ignore persistence errors.
        }
    }

    public static string? LoadGptPrompt()
    {
        try
        {
            if (!File.Exists(GptPromptFile))
            {
                return null;
            }

            var content = File.ReadAllText(GptPromptFile).Trim();
            return string.IsNullOrEmpty(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveGptPrompt(string? prompt)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);

            if (string.IsNullOrWhiteSpace(prompt))
            {
                File.WriteAllText(GptPromptFile, string.Empty);
            }
            else
            {
                File.WriteAllText(GptPromptFile, prompt);
            }
        }
        catch
        {
            // Ignore persistence errors.
        }
    }
}
