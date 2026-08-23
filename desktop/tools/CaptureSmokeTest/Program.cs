using DekhBhai.Core.Capture;

Console.WriteLine("Dekh Bhai capture smoke test starting...");

using var capture = new WindowsGraphicsScreenCapture();
int frameCount = 0;
byte[]? firstFrameBytes = null;
int firstFrameWidth = 0, firstFrameHeight = 0, firstFrameStride = 0;
var sw = System.Diagnostics.Stopwatch.StartNew();

capture.FrameArrived += (_, evt) =>
{
    if (evt.State != FrameState.ValidFrame || evt.Frame is not { } frame)
    {
        Console.WriteLine($"[event] state={evt.State} msg={evt.Message}");
        return;
    }

    frameCount++;
    if (frameCount == 1)
    {
        firstFrameBytes = frame.Data.ToArray();
        firstFrameWidth = frame.Width;
        firstFrameHeight = frame.Height;
        firstFrameStride = frame.Stride;
    }

    if (frameCount % 30 == 0)
    {
        Console.WriteLine($"frame #{frameCount} {frame.Width}x{frame.Height} stride={frame.Stride} hint={frame.ContentHint} t={frame.Timestamp:HH:mm:ss.fff}");
    }
};

capture.Start(new CaptureSettings { TargetWidth = 1920, TargetHeight = 1080, TargetFramesPerSecond = 30 });
Console.WriteLine("Capturing for 5 seconds. Move something on screen to verify live updates...");

while (sw.ElapsedMilliseconds < 5000)
{
    await Task.Delay(100);
}

capture.Stop();
Console.WriteLine($"Stopped. Total frames received: {frameCount}");

if (firstFrameBytes is not null)
{
    var outPath = Path.Combine(AppContext.BaseDirectory, "first-frame.bmp");
    WriteBmp(outPath, firstFrameBytes, firstFrameWidth, firstFrameHeight, firstFrameStride);
    Console.WriteLine($"Wrote {outPath}");
}

return frameCount > 0 ? 0 : 1;

static void WriteBmp(string path, byte[] bgraPixels, int width, int height, int stride)
{
    // Minimal 32bpp BMP writer (top-down not supported by BMP, so we flip rows), for
    // human-eyeball verification only - not part of the product.
    int rowBytes = width * 4;
    int imageSize = rowBytes * height;
    int fileSize = 14 + 40 + imageSize;

    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var bw = new BinaryWriter(fs);

    bw.Write((byte)'B'); bw.Write((byte)'M');
    bw.Write(fileSize);
    bw.Write(0); // reserved
    bw.Write(14 + 40); // pixel data offset

    bw.Write(40); // DIB header size
    bw.Write(width);
    bw.Write(height);
    bw.Write((short)1); // planes
    bw.Write((short)32); // bpp
    bw.Write(0); // compression
    bw.Write(imageSize);
    bw.Write(2835); bw.Write(2835); // ppm
    bw.Write(0); bw.Write(0);

    for (int y = height - 1; y >= 0; y--)
    {
        bw.Write(bgraPixels, y * stride, rowBytes);
    }
}
