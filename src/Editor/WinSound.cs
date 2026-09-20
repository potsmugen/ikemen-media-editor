using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Editor;

/// <summary>
/// In-process WAV playback on Windows through winmm's PlaySound, the call .NET's
/// SoundPlayer wraps. Plays asynchronously and can loop; one sound at a time.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinSound
{
    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004, SndLoop = 0x0008;

    // PlaySound reads the buffer while it plays, so it stays pinned until the next Play or Stop.
    private static GCHandle _pinned;

    public static void Play(byte[] wav, bool loop)
    {
        Stop();
        _pinned = GCHandle.Alloc(wav, GCHandleType.Pinned);
        uint flags = SndMemory | SndAsync | SndNoDefault | (loop ? SndLoop : 0);
        if (!PlaySound(_pinned.AddrOfPinnedObject(), IntPtr.Zero, flags))
        {
            _pinned.Free();
            throw new InvalidOperationException("Windows could not play this sound.");
        }
    }

    public static void Stop()
    {
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0); // stops whatever this process is playing
        if (_pinned.IsAllocated) _pinned.Free();
    }

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
