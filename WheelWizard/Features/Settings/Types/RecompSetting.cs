using System.Globalization;
using System.Text;

namespace WheelWizard.Settings.Types;

/// <summary>
/// A setting stored in the recomp's own <c>Config.toml</c>, which Wheel Wizard shares with the
/// in-game settings bar. Values are formatted exactly the way the runtime's own writer formats
/// them: booleans bare and lowercase, strings double-quoted, numbers invariant.
/// </summary>
public class RecompSetting : Setting
{
    private readonly Action<RecompSetting> _saveAction;

    public string Section { get; }

    public RecompSetting(Type type, (string Section, string Key) location, object defaultValue, Action<RecompSetting> saveAction)
        : base(type, location.Key, defaultValue)
    {
        _saveAction = saveAction ?? throw new ArgumentNullException(nameof(saveAction));
        Section = location.Section;
    }

    protected override bool SetInternal(object newValue, bool skipSave = false)
    {
        var oldValue = Value;
        Value = newValue;
        var newIsValid = SaveEvenIfNotValid || IsValid();
        if (newIsValid)
        {
            if (!skipSave)
                _saveAction(this);
        }
        else
            Value = oldValue;

        return newIsValid;
    }

    public override object Get() => Value;

    public override bool IsValid() => ValidationFunc == null || ValidationFunc(Value);

    public new RecompSetting SetValidation(Func<object?, bool> validationFunc)
    {
        base.SetValidation(validationFunc);
        return this;
    }

    public string GetStringValue() => WheelWizard.Core.RuntimeConfiguration.Format(Value);

    public bool SetFromString(string tomlValue, bool skipSave = false)
    {
        try
        {
            return Set(WheelWizard.Core.RuntimeConfiguration.Parse(tomlValue, ValueType), skipSave);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
