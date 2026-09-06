using System.ComponentModel;
using System.Runtime.CompilerServices;
using WheelWizard.Core.Mods;

namespace WheelWizard.Models.Mods;

public class Mod : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string _title = string.Empty;
    private string _author = string.Empty;
    private int _modID;
    private int _priority; // New property for mod priority
    private bool _hasIncompatibleFiles;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Author
    {
        get => _author;
        set => SetField(ref _author, value);
    }

    public int ModID
    {
        get => _modID;
        set => SetField(ref _modID, value);
    }

    public int Priority
    {
        get => _priority;
        set => SetField(ref _priority, value);
    }

    public bool HasIncompatibleFiles
    {
        get => _hasIncompatibleFiles;
        set => SetField(ref _hasIncompatibleFiles, value);
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public ModMetadata ToMetadata() => new(Title, Author, ModID, IsEnabled, Priority);

    public static Mod FromMetadata(ModMetadata value) =>
        new()
        {
            Title = value.Title,
            Author = value.Author,
            ModID = value.ModID,
            IsEnabled = value.IsEnabled,
            Priority = value.Priority,
        };

    /// <summary>Loads the mod details from an INI file.</summary>
    public static async Task<Mod> LoadFromIniAsync(string iniFilePath) =>
        await Task.FromResult(FromMetadata(ModMetadataFile.Load(iniFilePath)));

    /// <summary>Saves the mod details to an INI file.</summary>
    // Persistence lives in Core; retain this method for existing bound-model callers.
    public async Task SaveToIniAsync(string iniFilePath)
    {
        ModMetadataFile.Save(iniFilePath, ToMetadata());
        await Task.CompletedTask;
    }

    #region PropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
    }
    #endregion
}

public class ModData
{
    public bool IsEnabled { get; set; }
    public string Title { get; set; } = string.Empty;
}
