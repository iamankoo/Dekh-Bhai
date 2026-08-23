using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DekhBhai.Core.Audio;

/// <summary>
/// Captures the default microphone via WASAPI. Not wired into the Phase 1 host UI (mic
/// on/off control is a Phase 2 concern) but fully functional so SessionController can add a
/// second audio track later without touching the capture engine.
/// </summary>
public sealed class MicrophoneAudioCapture : IAudioCapture
{
    public event EventHandler<AudioChunk>? ChunkCaptured;
    public event EventHandler<string>? CaptureError;

    public bool IsCapturing { get; private set; }

    private WasapiCapture? _capture;

    public void Start()
    {
        if (IsCapturing) return;

        _capture = new WasapiCapture();
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        try
        {
            _capture.StartRecording();
            IsCapturing = true;
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(this, $"failed to start microphone capture: {ex.Message}");
            _capture.Dispose();
            _capture = null;
        }
    }

    public void Stop()
    {
        if (!IsCapturing) return;
        IsCapturing = false;
        _capture?.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null || e.BytesRecorded == 0) return;

        var format = _capture.WaveFormat;
        int bytesPerSample = format.BitsPerSample / 8;
        int sampleCount = e.BytesRecorded / bytesPerSample;
        var samples = new float[sampleCount];

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && bytesPerSample == 2)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(e.Buffer, i * 2);
                samples[i] = s / 32768f;
            }
        }
        else
        {
            return;
        }

        ChunkCaptured?.Invoke(this, new AudioChunk
        {
            Samples = samples,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            CaptureError?.Invoke(this, $"microphone capture stopped unexpectedly: {e.Exception.Message}");
        }
        IsCapturing = false;
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
        _capture = null;
    }
}
