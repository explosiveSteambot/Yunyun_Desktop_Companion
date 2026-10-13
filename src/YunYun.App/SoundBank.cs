using System.Runtime.InteropServices;

namespace YunYun.App;

/// <summary>
/// Short sound effects (all embedded sounds/*.wav), played at random with the user's volume.
///
/// Uses the Windows PlaySound API straight from memory - no extra packages, works on any Windows.
/// PlaySound has no volume control, so the volume is applied by scaling the 16-bit samples:
/// a scaled copy of every clip is kept in unmanaged memory (PlaySound reads it while playing
/// asynchronously, so it must not move or be freed) and rebuilt when the volume changes.
/// </summary>
sealed class SoundBank : IDisposable
{
    [DllImport("winmm.dll", SetLastError = true)]
    static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    const float NormalizedPeak = 0.9f;                 // -1 dB at 100 % volume
    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    readonly List<byte[]> _clips = new();             // original wav files
    readonly List<(IntPtr Ptr, int Len)> _scaled = new();
    readonly Random _rnd = new();
    int _last = -1;
    float _gain = 0.7f;

    public SoundBank()
    {
        foreach (var path in Assets.List("sounds", ".wav"))
            _clips.Add(Assets.ReadBytes(path));
        Rebuild();
    }

    public int Count => _clips.Count;

    /// <summary>volume 0..100 from the settings.</summary>
    public void SetVolume(int volume, bool muted)
    {
        float g = muted ? 0f : Math.Clamp(volume, 0, 100) / 100f;
        if (Math.Abs(g - _gain) < 1e-4f) return;
        _gain = g;
        Rebuild();
    }

    /// <summary>Plays one random sound, never the same one twice in a row.</summary>
    public void PlayRandom()
    {
        if (_scaled.Count == 0 || _gain <= 0f) return;
        int i;
        do i = _rnd.Next(_scaled.Count); while (_scaled.Count > 1 && i == _last);
        _last = i;
        PlaySound(_scaled[i].Ptr, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    void Rebuild()
    {
        Free();
        foreach (var wav in _clips)
        {
            var copy = (byte[])wav.Clone();
            ApplyGain(copy, _gain);
            var ptr = Marshal.AllocHGlobal(copy.Length);
            Marshal.Copy(copy, 0, ptr, copy.Length);
            _scaled.Add((ptr, copy.Length));
        }
    }

    /// <summary>Normalises and scales the samples of a 16-bit PCM wav in place (other formats are left as they are).</summary>
    static void ApplyGain(byte[] wav, float gain)
    {
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return;
        int pos = 12, format = 0, bits = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > wav.Length) size = wav.Length - body;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(wav, body);
                bits = BitConverter.ToUInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                if (format != 1 || bits != 16) return;            // only plain 16-bit PCM
                // The clips were cut from a video and are quiet (peaks around -17 dB):
                // bring every clip to the same peak first, then apply the user's volume.
                int peak = 1;
                for (int i = body; i + 1 < body + size; i += 2)
                    peak = Math.Max(peak, Math.Abs((int)(short)(wav[i] | wav[i + 1] << 8)));
                gain *= NormalizedPeak * short.MaxValue / peak;
                for (int i = body; i + 1 < body + size; i += 2)
                {
                    int v = (int)Math.Round((short)(wav[i] | wav[i + 1] << 8) * gain);
                    v = Math.Clamp(v, short.MinValue, short.MaxValue);
                    wav[i] = (byte)v;
                    wav[i + 1] = (byte)(v >> 8);
                }
                return;
            }
            pos = body + size + (size & 1);                       // chunks are word-aligned
        }
    }

    void Free()
    {
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);                  // stop: the buffers are about to be freed
        foreach (var (ptr, _) in _scaled) Marshal.FreeHGlobal(ptr);
        _scaled.Clear();
    }

    public void Dispose() => Free();
}
