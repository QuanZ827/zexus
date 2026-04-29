using System;
using System.IO;
using System.Text.Json;

namespace Zexus.Services
{
    public class AppConfig
    {
        // API Key must be configured by user on first launch via the Settings dialog
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "claude-sonnet-4-6";
        public int MaxTokens { get; set; } = 16384;
        public bool EnableStreaming { get; set; } = true;
        public string Provider { get; set; } = "Anthropic";
        public string Theme { get; set; } = "Dark";
    }

    public static class ConfigManager
    {
        private static AppConfig _config;
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zexus", "config.json");

        public static AppConfig Config
        {
            get
            {
                if (_config == null) LoadConfig();
                return _config;
            }
        }

        private static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    _config = JsonSerializer.Deserialize<AppConfig>(json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Failed to load config from {ConfigPath}: {ex.Message}");
            }

            if (_config == null)
            {
                _config = new AppConfig();
            }
        }

        private static void SaveConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Failed to save config to {ConfigPath}: {ex.Message}");
            }
        }

        public static bool IsConfigured()
        {
            return !string.IsNullOrEmpty(Config.ApiKey)
                && LlmProviderInfo.ValidateApiKey(GetProvider(), Config.ApiKey);
        }

        public static string GetApiKey() => Config.ApiKey;
        public static string GetModel() => Config.Model;

        public static LlmProvider GetProvider()
        {
            return LlmProviderInfo.Parse(Config.Provider);
        }

        public static void SetApiKey(string apiKey)
        {
            Config.ApiKey = apiKey;
            SaveConfig();
        }

        public static void SetModel(string model)
        {
            Config.Model = model;
            SaveConfig();
        }

        public static void SetProvider(LlmProvider provider)
        {
            Config.Provider = provider.ToString();
            // Update model to the new provider's default if current model belongs to another provider
            Config.Model = LlmProviderInfo.GetDefaultModel(provider);
            SaveConfig();
        }

        public static void SetTheme(string theme)
        {
            Config.Theme = theme;
            SaveConfig();
        }

    }
}
