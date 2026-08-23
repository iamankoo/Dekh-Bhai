using SIPSorceryMedia.FFmpeg;

namespace DekhBhai.Core.Media;

/// <summary>
/// One-time FFmpeg native library bootstrap. SIPSorceryMedia.FFmpeg's video encoder is a thin
/// managed wrapper over libavcodec/libavutil/libswscale, so those shared libraries must be
/// resolvable before any encoder is constructed. See
/// docs/architecture/phase-1-technology-decision.md for why FFmpeg is bundled this way.
/// </summary>
public static class FFmpegBootstrap
{
    private static bool _initialised;
    private static readonly object Lock = new();

    public static void EnsureInitialised(string? nativeLibraryDirectory = null)
    {
        lock (Lock)
        {
            if (_initialised) return;

            var dir = nativeLibraryDirectory ?? AppContext.BaseDirectory;
            FFmpegInit.Initialise(libPath: dir);
            _initialised = true;
        }
    }
}
