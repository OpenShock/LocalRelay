﻿using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShock.LocalRelay.Utils;
using OpenShock.SDK.CSharp.Hub.Utils;

namespace OpenShock.LocalRelay.Config;

public sealed class ConfigManager
{
    private static readonly string Path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\OpenShock\LocalRelay\config.json";
    
    private readonly ILogger<ConfigManager> _logger;
    public LocalRelayConfig Config { get; }
    private readonly Timer _saveTimer;

    public ConfigManager(ILogger<ConfigManager> logger)
    {
        _logger = logger;
        _saveTimer = new Timer(_ => { OsTask.Run(SaveInternally); });

        // Load config
        LocalRelayConfig? config = null;
        
        _logger.LogInformation("Config file found, trying to load config from {Path}", Path);
        if (File.Exists(Path))
        {
            _logger.LogTrace("Config file exists");
            var json = File.ReadAllText(Path);
            if (!string.IsNullOrWhiteSpace(json))
            {
                _logger.LogTrace("Config file is not empty");
                try
                {
                    config = JsonSerializer.Deserialize<LocalRelayConfig>(json, Options);
                }
                catch (Exception e)
                {
                    _logger.LogCritical(e, "Error during deserialization/loading of config");
                    _logger.LogWarning("Attempting to move old config and generate a new one");
                    File.Move(Path, Path + ".old");
                }
            }
        }

        if (config != null)
        {
            Config = config;
            _logger.LogInformation("Successfully loaded config");
            return;
        }
        _logger.LogInformation("No config file found (does not exist or empty or invalid), generating new one at {Path}", Path);
        Config = new LocalRelayConfig();
        SaveInternally().Wait();
        _logger.LogInformation("New configuration file generated!");
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new SemVersionJsonConverter() }
    };
    
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private async Task SaveInternally()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _logger.LogTrace("Saving config");
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(Path, JsonSerializer.Serialize(Config, Options)).ConfigureAwait(false);
            _logger.LogInformation("Config saved");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error occurred while saving new config file");
        }
        finally
        {
            _saveLock.Release();
        }
    }
    
    
    public void Save()
    {
        lock (_saveTimer)
        {
            _saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);            
        }
    }
    
}