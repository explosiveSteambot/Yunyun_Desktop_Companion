using System.Text.Json;

namespace YunYun.Core;

/// <summary>rig.json: layers, offsets, bones, draw order. See Assets/rig/rig.json.</summary>
public sealed class RigDefinition
{
    public int[] Size { get; set; } = { 628, 587 };
    public Dictionary<string, RigPart> Parts { get; set; } = new();
    public RigLayer Body { get; set; } = new();
    public RigLayer? Back { get; set; }
    public RigLayer? Cover { get; set; }
    public string[] DrawOrder { get; set; } = Array.Empty<string>();
    public int[] GrabPoint { get; set; } = { 224, 194 };

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static RigDefinition Parse(string json) =>
        JsonSerializer.Deserialize<RigDefinition>(json, Options) ?? throw new InvalidDataException("empty rig.json");
}

/// <summary>A static layer (body, skirt lining behind the legs, frill over the tail root).</summary>
public class RigLayer
{
    public string File { get; set; } = "";
    public int[] Offset { get; set; } = { 0, 0 };
}

/// <summary>A moving part: "chain" (hair, tail) or "rigid" (arms, legs, wing).</summary>
public sealed class RigPart : RigLayer
{
    public string Type { get; set; } = "rigid";
    public double[][]? Points { get; set; }      // chain: joint positions in canvas px, root first
    public double HalfWidth { get; set; }
    public double[]? SegHalfWidth { get; set; }  // chain: max distance of the strand from each segment
    public double[]? Pivot { get; set; }         // rigid: rotation centre in canvas px
}
