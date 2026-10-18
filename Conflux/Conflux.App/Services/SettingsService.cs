using System;
using System.IO;
using System.Text.Json;
using Conflux.Models;

namespace Conflux.Services;

public sealed class AppSettings
{
    public List<AiProvider> Providers { get; set; } = new()
    {
        new AiProvider
        {
            Name = "GPT",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4.1-mini"
        },
        new AiProvider
        {
            Name = "Claude",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = ""
        },
        new AiProvider
        {
            Name = "Gemini",
            BaseUrl = "",
            Model = ""
        },
        new AiProvider
        {
            Name = "Local",
            BaseUrl = "http://localhost:8080/v1",
            Model = ""
        }
    };

    public List<RoutingProfile> Routes { get; set; } = new()
    {
        new RoutingProfile
        {
            TaskType = "General",
            PrimaryProvider = "GPT",
            SecondaryProvider = "Local",
            EnableReview = false
        },
        new RoutingProfile
        {
            TaskType = "Coding",
            PrimaryProvider = "Claude",
            SecondaryProvider = "GPT",
            EnableReview = true
        },
        new RoutingProfile
        {
            TaskType = "Images",
            PrimaryProvider = "GPT",
            SecondaryProvider = "",
            EnableReview = false
        },
        new RoutingProfile
        {
            TaskType = "Writing",
            PrimaryProvider = "Gemini",
            SecondaryProvider = "GPT",
            EnableReview = true
        }
    };
}

public sealed class SettingsService
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "Conflux");

        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_settingsPath))
            return new AppSettings();

        try
        {
            await using var stream = File.OpenRead(_settingsPath);

            return await JsonSerializer.DeserializeAsync<AppSettings>(
                stream, _jsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Preserve the invalid file for recovery rather than
            // overwriting it automatically.
            throw new InvalidOperationException(
                $"Settings could not be read. Check {_settingsPath}.");
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var temporaryPath = _settingsPath + ".tmp";

        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream, settings, _jsonOptions);
        }

        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}