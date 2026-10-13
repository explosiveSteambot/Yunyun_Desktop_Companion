using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YunYun.Core;

/// <summary>
/// Packed resources (res.dat): every picture, sound and the rig in one file that is not an archive
/// any program can open - the contents are compressed and mixed with a key stream, so there are no
/// PNG/WAV files, signatures or file names inside. Read straight into memory.
///
/// The format is shared with the Python version (yunyun/assets.py), so one res.dat serves both.
///     MAGIC (8 bytes) | version u32 | nonce u64 | payload                    (little-endian)
///     payload  = zlib(index_len u32 | index json | blobs)  XOR  keystream
///     index    = {"path": [offset, size], ...}, offsets relative to the end of the index
///     nonce    = first 8 bytes of sha256(zlib data)  (same contents -> byte-identical file)
///     keystream: seed = first 8 bytes of sha256(KEY | nonce) as u64,
///                word i (i = 1, 2, ...) = splitmix64(seed + i * 0x9E3779B97F4A7C15), bytes little-endian
///
/// It keeps the resources out of sight; it is not protection against someone determined
/// (the key is in the source, as with any program that has to show its own pictures).
/// </summary>
public sealed class ResourcePack
{
    static readonly byte[] Magic = { 0x93, (byte)'Y', (byte)'Y', (byte)'R', (byte)'E', (byte)'S', (byte)'\r', (byte)'\n' };
    const uint Version = 2;
    const int HeaderSize = 8 + 4 + 8;
    static readonly byte[] Key = Encoding.ASCII.GetBytes("yunyun-desktop/they-are-just-pictures");

    readonly Dictionary<string, (int Offset, int Size)> _index;
    readonly byte[] _body;
    readonly int _dataStart;

    ResourcePack(Dictionary<string, (int, int)> index, byte[] body, int dataStart)
    {
        _index = index;
        _body = body;
        _dataStart = dataStart;
    }

    // ------------------------------------------------------------------ reading
    public static ResourcePack Load(byte[] raw)
    {
        if (raw.Length < HeaderSize || !raw.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("not a resource pack");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
        if (version != Version) throw new InvalidDataException($"unsupported resource pack version {version}");
        ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(12));

        var compressed = raw.AsSpan(HeaderSize).ToArray();
        Xor(compressed, nonce);
        byte[] body;
        using (var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
        using (var m = new MemoryStream())
        {
            z.CopyTo(m);
            body = m.ToArray();
        }

        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
        var index = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(body.AsMemory(4, n)))
            foreach (var p in doc.RootElement.EnumerateObject())
                index[p.Name] = (p.Value[0].GetInt32(), p.Value[1].GetInt32());
        return new ResourcePack(index, body, 4 + n);
    }

    public static ResourcePack Load(Stream s)
    {
        using var m = new MemoryStream();
        s.CopyTo(m);
        return Load(m.ToArray());
    }

    public IEnumerable<string> Names => _index.Keys.OrderBy(k => k, StringComparer.Ordinal);

    public bool Contains(string path) => _index.ContainsKey(path);

    /// <summary>Bytes of one packed file, path like "rig/body.png".</summary>
    public byte[] Read(string path)
    {
        if (!_index.TryGetValue(path, out var e)) throw new FileNotFoundException("resource not found", path);
        return _body.AsSpan(_dataStart + e.Offset, e.Size).ToArray();
    }

    public Stream Open(string path) => new MemoryStream(Read(path), writable: false);

    /// <summary>Sorted paths directly inside a folder, e.g. List("idle", ".png").</summary>
    public IReadOnlyList<string> List(string folder, string ext = "")
    {
        string pre = folder.TrimEnd('/') + "/";
        return Names.Where(p => p.StartsWith(pre, StringComparison.Ordinal)
                                && p.IndexOf('/', pre.Length) < 0
                                && p.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    .ToList();
    }

    // ------------------------------------------------------------------ writing (tools/PackAssets)
    /// <summary>Packs every file under srcDir (skipping dot-files, .txt and .md). Returns the file count.</summary>
    public static int Pack(string srcDir, string outPath)
    {
        var files = Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories)
            .Select(f => (Rel: Path.GetRelativePath(srcDir, f).Replace('\\', '/'), Full: f))
            .Where(f => !Path.GetFileName(f.Rel).StartsWith('.')
                        && !f.Rel.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                        && !f.Rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Rel, StringComparer.Ordinal)
            .ToList();

        var index = new Dictionary<string, int[]>();
        var blobs = new MemoryStream();
        foreach (var (rel, full) in files)
        {
            var data = File.ReadAllBytes(full);
            index[rel] = new[] { (int)blobs.Length, data.Length };
            blobs.Write(data);
        }
        var idx = JsonSerializer.SerializeToUtf8Bytes(index);

        var plain = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)idx.Length);
        plain.Write(len);
        plain.Write(idx);
        blobs.Position = 0;
        blobs.CopyTo(plain);

        var compressedStream = new MemoryStream();
        using (var z = new ZLibStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(plain.GetBuffer(), 0, (int)plain.Length);
        var payload = compressedStream.ToArray();

        ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(payload));
        Xor(payload, nonce);

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12), nonce);

        string tmp = outPath + ".tmp";
        using (var f = File.Create(tmp))
        {
            f.Write(header);
            f.Write(payload);
        }
        File.Move(tmp, outPath, overwrite: true);
        return files.Count;
    }

    // ------------------------------------------------------------------ key stream
    internal static void Xor(Span<byte> data, ulong nonce)
    {
        Span<byte> seedInput = stackalloc byte[Key.Length + 8];
        Key.CopyTo(seedInput);
        BinaryPrimitives.WriteUInt64LittleEndian(seedInput[Key.Length..], nonce);
        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(seedInput));

        for (int w = 0; w * 8 < data.Length; w++)
        {
            ulong z = KeyWord(seed, (ulong)w + 1);
            int end = Math.Min(8, data.Length - w * 8);
            for (int b = 0; b < end; b++)
                data[w * 8 + b] ^= (byte)(z >> (8 * b));
        }
    }

    internal static ulong KeyWord(ulong seed, ulong i)
    {
        unchecked
        {
            ulong z = seed + i * 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
