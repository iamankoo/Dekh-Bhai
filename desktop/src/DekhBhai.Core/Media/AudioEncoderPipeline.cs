using Concentus;
using Concentus.Enums;
using DekhBhai.Core.Audio;

namespace DekhBhai.Core.Media;

/// <summary>
/// Bridges an audio capture source to an Opus encoder. Per RFC 7587 the Opus RTP clock is
/// always 48kHz regardless of the encoder's internal rate, which is also WASAPI's near-universal
/// default mix rate on Windows - so Phase 1 targets 48kHz/stereo directly rather than resampling.
/// </summary>
public sealed class AudioEncoderPipeline : IDisposable
{
    public const int OpusClockRate = 48000;
    private const int FrameSizeSamplesPerChannel = 960; // 20ms @ 48kHz

    /// <summary>Encoded Opus frame + its RTP duration in 48kHz units (always 960 for a 20ms frame).</summary>
    public event Action<byte[], uint>? EncodedSampleReady;
    public event EventHandler<string>? EncodeError;

    private readonly IAudioCapture _capture;
    private IOpusEncoder? _encoder;
    private int _channels;
    private readonly List<float> _pending = new();
    private readonly byte[] _outBuffer = new byte[4000];

    public AudioEncoderPipeline(IAudioCapture capture)
    {
        _capture = capture;
        _capture.ChunkCaptured += OnChunk;
    }

    private void OnChunk(object? sender, AudioChunk chunk)
    {
        if (chunk.SampleRate != OpusClockRate)
        {
            EncodeError?.Invoke(this, $"unsupported device sample rate {chunk.SampleRate}Hz (Phase 1 expects 48000Hz); dropping audio buffer");
            return;
        }

        if (_encoder is null)
        {
            _channels = chunk.Channels;
            _encoder = OpusCodecFactory.CreateEncoder(OpusClockRate, _channels, OpusApplication.OPUS_APPLICATION_AUDIO, messageLogger: null!);
        }
        else if (chunk.Channels != _channels)
        {
            return; // device format changed mid-session; skip until the pipeline restarts
        }

        _pending.AddRange(chunk.Samples);

        int samplesNeeded = FrameSizeSamplesPerChannel * _channels;
        while (_pending.Count >= samplesNeeded)
        {
            var frame = _pending.GetRange(0, samplesNeeded).ToArray();
            _pending.RemoveRange(0, samplesNeeded);
            EncodeFrame(frame);
        }
    }

    private void EncodeFrame(float[] frame)
    {
        try
        {
            int bytesEncoded = _encoder!.Encode(frame.AsSpan(), FrameSizeSamplesPerChannel, _outBuffer.AsSpan(), _outBuffer.Length);
            if (bytesEncoded > 0)
            {
                var encoded = new byte[bytesEncoded];
                Array.Copy(_outBuffer, encoded, bytesEncoded);
                EncodedSampleReady?.Invoke(encoded, FrameSizeSamplesPerChannel);
            }
        }
        catch (Exception ex)
        {
            EncodeError?.Invoke(this, $"opus encode failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _capture.ChunkCaptured -= OnChunk;
    }
}
