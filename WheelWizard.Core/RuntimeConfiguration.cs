using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;

namespace WheelWizard.Core;

public record ConfigurationValue(string Section, string Key, string Literal);

public record RuntimeSettings(double Volume = 1, double ResolutionMultiplier = 1);

// A line-preserving editor for the runtime's flat TOML sections. Unknown lines are never rewritten.
public sealed class RuntimeConfiguration(IFileSystem files)
{
    public static string Format(object value) =>
        value switch
        {
            string s => JsonSerializer.Serialize(
                s,
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
            ),
            bool b => b ? "true" : "false",
            double d when double.IsFinite(d) => d.ToString("0.0###", CultureInfo.InvariantCulture),
            _ => throw new FormatException("Unsupported configuration value"),
        };

    public static object Parse(string literal, Type type)
    {
        literal = literal.Trim();
        if (type == typeof(string))
        {
            if (literal.Length >= 2 && literal[0] == '\'' && literal[^1] == '\'')
                return literal[1..^1];
            if (literal.StartsWith('"'))
                return JsonSerializer.Deserialize<string>(literal) ?? throw new FormatException("Null string");
        }
        if (type == typeof(bool) && literal is "true" or "false")
            return literal == "true";
        if (
            type == typeof(double)
            && double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.IsFinite(n)
        )
            return n;
        throw new FormatException($"Malformed {type.Name} value: {literal}");
    }

    static string StripComment(string value)
    {
        char quote = '\0';
        bool escaped = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (quote == '"' && c == '\\')
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '#')
                return value[..i].Trim();
        }
        return value.Trim();
    }

    public string? Read(string path, string section, string key)
    {
        if (!files.File.Exists(path))
            return null;
        string current = "";
        string? found = null;
        foreach (var line in files.File.ReadAllLines(path))
        {
            var s = StripComment(line);
            if (s.StartsWith('[') && s.EndsWith(']'))
            {
                current = s[1..^1];
                continue;
            }
            var eq = s.IndexOf('=');
            if (current != section || eq < 0 || s[..eq].Trim() != key)
                continue;
            if (found != null)
                throw new FormatException($"Duplicate setting {section}.{key}");
            found = s[(eq + 1)..].Trim();
        }
        return found;
    }

    public void Write(string path, IEnumerable<ConfigurationValue> changes, bool create = false)
    {
        if (!files.File.Exists(path) && !create)
            return;
        var lines = files.File.Exists(path) ? files.File.ReadAllLines(path).ToList() : [];
        foreach (var change in changes)
        {
            // Reject malformed supported old values before modifying anything.
            var old = Read(path, change.Section, change.Key);
            if (old != null)
            {
                if (change.Literal == "[]")
                {
                    try
                    {
                        var values = JsonSerializer.Deserialize<string[]>(old);
                        if (values == null || values.Any(v => v == null))
                            throw new FormatException("Malformed string array");
                    }
                    catch (JsonException e)
                    {
                        throw new FormatException($"Malformed {change.Section}.{change.Key}: {old}", e);
                    }
                }
                else
                    Parse(
                        old,
                        change.Literal.StartsWith('"') ? typeof(string)
                            : change.Literal is "true" or "false" ? typeof(bool)
                            : typeof(double)
                    );
            }
            int section = lines.FindIndex(l => StripComment(l) == $"[{change.Section}]");
            if (section < 0)
            {
                lines.Add($"[{change.Section}]");
                section = lines.Count - 1;
            }
            int end = section + 1;
            while (end < lines.Count && !StripComment(lines[end]).StartsWith('['))
                end++;
            int index = lines.FindIndex(
                section + 1,
                end - section - 1,
                l =>
                {
                    var eq = l.IndexOf('=');
                    return eq >= 0 && l[..eq].Trim() == change.Key;
                }
            );
            var replacement = $"{change.Key} = {change.Literal}";
            if (index >= 0)
                lines[index] = replacement;
            else
                lines.Insert(end, replacement);
        }
        files.Directory.CreateDirectory(files.Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            files.File.WriteAllLines(temp, lines);
            files.File.Move(temp, path, true);
        }
        finally
        {
            if (files.File.Exists(temp))
                files.File.Delete(temp);
        }
    }

    public void Remove(string path, string section, string key)
    {
        if (!files.File.Exists(path))
            return;
        string current = "";
        var lines = files
            .File.ReadAllLines(path)
            .Where(line =>
            {
                var s = StripComment(line);
                if (s.StartsWith('[') && s.EndsWith(']'))
                    current = s[1..^1];
                int eq = s.IndexOf('=');
                return current != section || eq < 0 || s[..eq].Trim() != key;
            });
        files.File.WriteAllLines(path, lines.ToArray());
    }

    public RuntimeSettings ReadSettings(string path) =>
        new(Number(path, "audio", "volume", 1), Number(path, "video", "resolution_multiplier", 1));

    double Number(string path, string section, string key, double fallback) =>
        Read(path, section, key) is { } v ? (double)Parse(v, typeof(double)) : fallback;

    public void WriteSettings(string path, RuntimeSettings settings)
    {
        if (
            !double.IsFinite(settings.Volume)
            || settings.Volume < 0
            || settings.Volume > 1
            || !double.IsFinite(settings.ResolutionMultiplier)
            || settings.ResolutionMultiplier < 1
            || settings.ResolutionMultiplier > 3
        )
            throw new ArgumentException("Volume must be 0–1 and resolution multiplier 1–3");
        Write(
            path,
            [new("audio", "volume", Format(settings.Volume)), new("video", "resolution_multiplier", Format(settings.ResolutionMultiplier))],
            true
        );
    }
}
