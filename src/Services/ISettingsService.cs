using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public interface ISettingsService
    {
        UserSettings LoadSettings();
        bool SaveSettings(UserSettings settings);
        void EnsureDefaultCustomProviders(UserSettings settings);
        void SetApiKeyForProvider(string provider, string key, UserSettings settings);
    }
}
