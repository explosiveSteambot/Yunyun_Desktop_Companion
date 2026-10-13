// Packs assets_src/ (layers, rig.json, idle frames, reaction pictures, sounds) into Assets/res.dat,
// which YunYun.App embeds into the exe. Run after changing anything in assets_src/:
//
//     dotnet run --project tools/PackAssets                 (from the repository root)
//     dotnet run --project tools/PackAssets -- <src> <out>  (explicit paths)
//
// The output is deterministic: unchanged sources give a byte-identical res.dat (nothing new for git).
// The format is shared with the Python version, so the same res.dat works there too (yunyun/res.dat).
using YunYun.Core;

static string FindRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "YunYunDesktop.sln")))
                return d.FullName;
    throw new DirectoryNotFoundException("YunYunDesktop.sln not found - pass <src> <out> explicitly");
}

string src, outPath;
if (args.Length == 2) { src = args[0]; outPath = args[1]; }
else
{
    var root = FindRoot();
    src = Path.Combine(root, "assets_src");
    outPath = Path.Combine(root, "Assets", "res.dat");
}

if (!Directory.Exists(src))
{
    Console.Error.WriteLine($"No sources: {src}\nPut rig/, idle/, reactions/, sounds/ there (they are not in git).");
    return 1;
}

int n = ResourcePack.Pack(src, outPath);
var pack = ResourcePack.Load(File.ReadAllBytes(outPath));          // read it back to be sure
if (pack.Names.Count() != n) throw new InvalidDataException("pack check failed");
Console.WriteLine($"{n} files -> {outPath} ({new FileInfo(outPath).Length / 1e6:F1} MB)");
return 0;
