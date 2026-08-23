using DekhBhai.Core.Capture;

namespace DekhBhai.Core.Tests;

/// <summary>
/// Covers the cheap, informational-only black-frame sampling heuristic used purely for the host
/// status line (see FrameContentHint's docs) - it must never influence whether a frame is sent.
/// </summary>
public class FrameContentHintTests
{
    private const int Width = 4;
    private const int Height = 4;
    private const int Stride = Width * 4; // tightly packed BGRA

    [Fact]
    public void AllBlackBuffer_IsClassifiedLikelyBlack()
    {
        var buffer = new byte[Stride * Height]; // all zeros = pure black, opaque or not

        var hint = WindowsGraphicsScreenCapture.SampleContentHint(buffer, Stride, Width, Height);

        Assert.Equal(FrameContentHint.LikelyBlack, hint);
    }

    [Fact]
    public void BufferWithVisibleContentInCenter_IsClassifiedNormal()
    {
        var buffer = new byte[Stride * Height];
        // Center pixel (one of the sampled points) set to a bright color.
        int centerOffset = (Height / 2) * Stride + (Width / 2) * 4;
        buffer[centerOffset] = 200; // B

        var hint = WindowsGraphicsScreenCapture.SampleContentHint(buffer, Stride, Width, Height);

        Assert.Equal(FrameContentHint.Normal, hint);
    }

    [Fact]
    public void DoesNotThrow_WhenBufferSmallerThanExpected()
    {
        // Defensive: a truncated/garbage buffer must not crash a purely informational sample.
        var buffer = new byte[2];

        var hint = WindowsGraphicsScreenCapture.SampleContentHint(buffer, Stride, Width, Height);

        Assert.Equal(FrameContentHint.LikelyBlack, hint);
    }
}
