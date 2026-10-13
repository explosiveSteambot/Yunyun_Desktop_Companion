using YunYun.Core;
using Xunit;

namespace YunYun.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void MissingOrBrokenFileGivesDefaults()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "sub", "settings.json");
        Assert.Equal(0.6, SettingsStore.Load(path).Scale);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{broken");
        Assert.Equal(Layers.Desktop, SettingsStore.Load(path).Layer);
    }

    [Fact]
    public void RoundTripAndClamp()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "s.json");
        SettingsStore.Save(path, new Settings { IdleScale = 0.4, Volume = 30, Position = new[] { 10, 20 } });
        var s = SettingsStore.Load(path);
        Assert.Equal(0.4, s.IdleScale);
        Assert.Equal(30, s.Volume);
        Assert.Equal(new[] { 10, 20 }, s.Position);

        File.WriteAllText(path, "{\"scale\": 5, \"volume\": 250, \"fps\": 1, \"layer\": \"x\", \"junk\": 1}");
        s = SettingsStore.Load(path);
        Assert.Equal(1.0, s.Scale);
        Assert.Equal(100, s.Volume);
        Assert.Equal(15, s.Fps);
        Assert.Equal(Layers.Desktop, s.Layer);
    }
}

public class GeometryTests
{
    static readonly RectI Area = new(0, 0, 1920, 1040);    // 1080 screen minus a 40 px taskbar

    [Fact]
    public void ClampKeepsOffTheTaskbar()
    {
        Assert.Equal(new RectI(1500, 740, 400, 300), ScreenGeometry.ClampRect(new RectI(1500, 900, 400, 300), Area));
        var r = ScreenGeometry.ClampRect(new RectI(1500, 900, 400, 300), Area, new RectI(50, 20, 300, 200));
        Assert.Equal(1040, r.Y + 20 + 200);                 // only the opaque part must stay inside
        Assert.Equal(new RectI(0, 0, 400, 300), ScreenGeometry.ClampRect(new RectI(-100, -50, 400, 300), Area));
    }

    [Fact]
    public void PickAreaMultiMonitor()
    {
        var areas = new[] { Area, new RectI(1920, 0, 2560, 1400) };
        Assert.Equal(areas[1], ScreenGeometry.PickArea(areas, 2000, 100));
        Assert.Equal(areas[0], ScreenGeometry.PickArea(areas, 1000, 1070));
        Assert.Equal(areas[1], ScreenGeometry.PickArea(areas, 5000, 100));
    }
}

public class RagdollTests
{
    // A tiny synthetic rig: an opaque body, one rigid part and one 3-link chain.
    static Ragdoll Make()
    {
        var rig = new RigDefinition
        {
            Size = new[] { 64, 64 },
            Body = new RigLayer { File = "body", Offset = new[] { 0, 0 } },
            DrawOrder = new[] { "body", "arm", "hair" },
            Parts = new()
            {
                ["arm"] = new RigPart { File = "arm", Type = "rigid", Offset = new[] { 30, 10 }, Pivot = new[] { 32.0, 10 } },
                ["hair"] = new RigPart
                {
                    File = "hair", Type = "chain", Offset = new[] { 8, 8 }, HalfWidth = 4, SegHalfWidth = new[] { 4.0, 4, 4 },
                    Points = new[] { new[] { 10.0, 10 }, new[] { 10.0, 20 }, new[] { 10.0, 30 }, new[] { 10.0, 40 } },
                },
            },
        };
        RgbaImage Solid(int w, int h, float v)
        {
            var img = new RgbaImage(w, h);
            for (int i = 0; i < w * h; i++) { img.Data[i * 4] = v; img.Data[i * 4 + 3] = v; }
            return img;
        }
        var p = new Dictionary<string, PartParams>
        {
            ["arm"] = new(new DriveParams(0.3, 0, 8, 4, 0.05)),
            ["hair"] = new(new DriveParams(0.002, 0, 1.0, 3.0, 0.3), MaxBend: new[] { 20.0 }),
        };
        return new Ragdoll(rig, f => f switch { "body" => Solid(64, 64, 0.5f), "arm" => Solid(4, 20, 1f), _ => Solid(5, 34, 1f) }, p);
    }

    [Fact]
    public void RestPoseIsAnExactCopy()
    {
        var rd = Make();
        var c = rd.Render();
        // the chain at rest maps every pixel onto itself: (10, 25) is inside the hair image -> fully opaque red
        int o = (25 * 64 + 10) * 4;
        Assert.Equal(1f, c.Data[o + 3], 3);
        Assert.Equal(1f, c.Data[o], 3);
    }

    [Fact]
    public void PartsLeanAgainstTheMotionAndRespectLimits()
    {
        var rd = Make();
        double x = 0;
        for (int i = 0; i < 300; i++) { x -= 600.0 / 60; rd.Step(1 / 60.0, x, 0); }   // drag left for 5 s
        var arm = (RigidPart)rd.Parts["arm"];
        Assert.True(arm.Angle < 0);                                  // kx > 0 and vx < 0
        Assert.True(Math.Abs(arm.Angle) <= 8 * Math.PI / 180 + 1e-9); // stops at Max
        for (int i = 0; i < 600; i++) rd.Step(1 / 60.0, x, 0);       // stop for 10 s
        Assert.True(Math.Abs(arm.Angle) < 1e-6);                     // back to rest
    }

    [Fact]
    public void ResamplerKeepsSolidColour()
    {
        var img = new RgbaImage(10, 10);
        for (int i = 0; i < 100; i++) { img.Data[i * 4 + 1] = 1; img.Data[i * 4 + 3] = 1; }
        foreach (var (w, h) in new[] { (6, 6), (15, 15) })
        {
            var buf = new byte[w * h * 4];
            new Resampler(10, 10, w, h).Run(img, buf);
            Assert.All(Enumerable.Range(0, w * h), i => { Assert.Equal(255, buf[i * 4 + 1]); Assert.Equal(255, buf[i * 4 + 3]); });
        }
    }
}

public class ResourcePackTests
{
    // Built by the Python version (yunyun/assets.py) from: rig/rig.json, idle/idle_00.png, idle/idle_01.png
    // (+ idle/info.txt, which the packer skips). Proves both programs read the same res.dat.
    const string PythonPack =
        "k1lZUkVTDQoCAAAAOKBZYZbO42nFCZnVX0a8xs1mO3QXhl4c8EhVoQFJ2KUNCqwjRpXKikVccGcjI2+0/HGOMyHOLP6bhZJcZBRqbYdyv6InvKnMrXsOPsze3UEqJWloOHzHZafCwV2E";

    static string Text(byte[] b) => System.Text.Encoding.UTF8.GetString(b);

    [Fact]
    public void ReadsPackMadeByPython()
    {
        var p = ResourcePack.Load(Convert.FromBase64String(PythonPack));
        Assert.Equal(new[] { "idle/idle_00.png", "idle/idle_01.png", "rig/rig.json" }, p.Names);
        Assert.Equal("{\"size\":[2,1]}", Text(p.Read("rig/rig.json")));
        Assert.Equal("frame-1", Text(p.Read("idle/idle_01.png")));
        Assert.Equal(new[] { "idle/idle_00.png", "idle/idle_01.png" }, p.List("idle", ".png"));
        Assert.Empty(p.List("rig", ".png"));
        Assert.Throws<FileNotFoundException>(() => p.Read("nope.png"));
    }

    [Fact]
    public void KeyStreamMatchesPython()
    {
        var zeros = new byte[16];
        ResourcePack.Xor(zeros, 0);                       // xor with zeros = the key stream itself
        Assert.Equal("b95084d2f747b835b27b3423ca3d7940", Convert.ToHexString(zeros).ToLowerInvariant());
    }

    [Fact]
    public void PackRoundTripIsDeterministicAndOpaque()
    {
        var src = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(src, "sounds"));
        var wav = new byte[1000];
        "RIFF"u8.CopyTo(wav); "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        File.WriteAllBytes(Path.Combine(src, "sounds", "a.wav"), wav);
        File.WriteAllText(Path.Combine(src, "sounds", "README.txt"), "skipped");
        var out1 = Path.Combine(src, "..", Path.GetRandomFileName());
        var out2 = out1 + "2";
        Assert.Equal(1, ResourcePack.Pack(src, out1));
        ResourcePack.Pack(src, out2);
        var raw = File.ReadAllBytes(out1);
        Assert.Equal(raw, File.ReadAllBytes(out2));                               // same contents -> same file
        Assert.Equal(-1, raw.AsSpan().IndexOf("WAVEfmt "u8));                     // no signatures inside
        Assert.Equal(-1, raw.AsSpan().IndexOf("a.wav"u8));                        // not even file names
        Assert.Equal(wav, ResourcePack.Load(raw).Read("sounds/a.wav"));
    }
}
