using System.Text.Json;
using System.Text.Json.Serialization;

namespace YunYun.Core;

/// <summary>Where the companion window lives relative to other windows.</summary>
public static class Layers
{
    public const string Desktop = "desktop"; // on the desktop, under ordinary windows
    public const string Normal = "normal";   // like a normal window
    public const string Top = "top";         // always on top
    public static readonly string[] All = { Desktop, Normal, Top };
}

/// <summary>User settings, stored as JSON.</summary>
public sealed class Settings
{
    public double Scale { get; set; } = 0.6;        // size while dragged (0.3 .. 1.0)
    public double IdleScale { get; set; } = 0.6;    // size of the resting animation (0.2 .. 1.0)
    public int Volume { get; set; } = 70;           // 0 .. 100
    public bool Muted { get; set; }
    public int Fps { get; set; } = 60;              // 15 .. 120
    public string Layer { get; set; } = Layers.Desktop;
    public bool StartHidden { get; set; }
    public bool RememberPosition { get; set; } = true;
    public int[]? Position { get; set; }            // last resting window position [x, y], physical px
    public bool Autostart { get; set; }             // mirrors the registry entry (App/Autostart.cs)
    public int[]? BinRect { get; set; }             // manual Recycle Bin rect [x, y, w, h] (testing / override)

    public Settings Clamp()
    {
        Scale = Math.Clamp(Scale, 0.3, 1.0);
        IdleScale = Math.Clamp(IdleScale, 0.2, 1.0);
        Volume = Math.Clamp(Volume, 0, 100);
        Fps = Math.Clamp(Fps, 15, 120);
        if (Array.IndexOf(Layers.All, Layer) < 0) Layer = Layers.Desktop;
        if (Position is { Length: not 2 }) Position = null;
        if (BinRect is { Length: not 4 }) BinRect = null;
        return this;
    }

    public Settings Clone()
    {
        var c = (Settings)MemberwiseClone();
        c.Position = Position?.ToArray();
        c.BinRect = BinRect?.ToArray();
        return c;
    }
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Missing or broken file -> defaults; unknown keys are ignored.</summary>
    public static Settings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Settings();
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options);
            return (s ?? new Settings()).Clamp();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    /// <summary>Atomic save: write a temp file, then replace.</summary>
    public static void Save(string path, Settings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s.Clone().Clamp(), Options));
        File.Move(tmp, path, overwrite: true);
    }
}
