using System.IO.Abstractions;
using WheelWizard.Services;
using WheelWizard.Settings.Types;

namespace WheelWizard.Settings;

/// <summary>
/// Reads and writes the recomp's <c>Config.toml</c> the same way <see cref="DolphinSettingManager"/>
/// handles Dolphin's ini files. The file has two writers
/// </summary>
public class RecompSettingManager(IFileSystem fileSystem) : IRecompSettingManager
{
    private readonly object _syncRoot = new();
    private readonly object _fileIoSync = new();
    private bool _loaded;
    private readonly List<RecompSetting> _settings = [];

    public void RegisterSetting(RecompSetting setting)
    {
        lock (_syncRoot)
        {
            if (_loaded)
                return;

            _settings.Add(setting);
        }
    }

    public void SaveSettings(RecompSetting invokingSetting)
    {
        lock (_syncRoot)
        {
            if (!_loaded)
                return;
        }

        lock (_fileIoSync)
        {
            WriteTomlSetting(invokingSetting.Section, invokingSetting.Name, invokingSetting.GetStringValue());
        }
    }

    public void ReloadSettings()
    {
        lock (_syncRoot)
        {
            _loaded = false;
        }

        LoadSettings();
    }

    public void RemoveTomlSetting(string section, string settingToRemove)
    {
        lock (_fileIoSync)
        {
            new WheelWizard.Core.RuntimeConfiguration(fileSystem).Remove(PathManager.RecompConfigFilePath, section, settingToRemove);
        }
    }

    public void LoadSettings()
    {
        List<RecompSetting> settingsSnapshot;
        if (_loaded || !fileSystem.File.Exists(PathManager.RecompConfigFilePath))
            return;

        lock (_syncRoot)
        {
            if (_loaded)
                return;

            _loaded = true;
            settingsSnapshot = [.. _settings];
        }

        lock (_fileIoSync)
        {
            Errors.Clear();
            foreach (var setting in settingsSnapshot)
            {
                // A missing or unparsable key keeps the registered default without writing it back:
                // the runtime falls back to the very same default, so the file stays untouched until
                // the user actually changes something.
                try
                {
                    var value = ReadTomlSetting(setting.Section, setting.Name);
                    if (value != null && !setting.SetFromString(value, true))
                        Errors.Add($"Malformed setting {setting.Section}.{setting.Name}: {value}");
                }
                catch (FormatException e)
                {
                    Errors.Add(e.Message);
                }
            }
        }
    }

    public List<string> Errors { get; } = [];

    private string? ReadTomlSetting(string section, string key) =>
        new WheelWizard.Core.RuntimeConfiguration(fileSystem).Read(PathManager.RecompConfigFilePath, section, key);

    private void WriteTomlSetting(string section, string key, string value) =>
        new WheelWizard.Core.RuntimeConfiguration(fileSystem).Write(PathManager.RecompConfigFilePath, [new(section, key, value)]);
}
