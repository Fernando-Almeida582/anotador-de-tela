using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using Gdi = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace Anotador;

internal static class ScreenRecording
{
    private const int FramesPerSecond = 15;

    internal sealed record Result(string Path, BitmapSource Preview);

    internal static Result Record(Gdi.Rectangle bounds, string folder, CancellationToken stop)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"Gravacao-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.avi");
        string temporary = path + ".tmp";
        try
        {
            BitmapImage preview;
            using (var video = new MjpegAvi(temporary, bounds.Size))
            using (var frame = new Gdi.Bitmap(bounds.Width, bounds.Height, Imaging.PixelFormat.Format32bppRgb))
            using (var jpeg = new MemoryStream())
            {
                Native.DwmFlush();
                var clock = Stopwatch.StartNew();
                long frameNumber = 0;
                do
                {
                    Screenshot.CopyDesktop(frame, bounds);
                    jpeg.SetLength(0);
                    frame.Save(jpeg, Imaging.ImageFormat.Jpeg);
                    var data = jpeg.GetBuffer().AsSpan(0, (int)jpeg.Length);
                    long currentFrame = clock.ElapsedMilliseconds * FramesPerSecond / 1000;
                    do
                    {
                        if (!video.TryWriteFrame(data)) goto Complete;
                        frameNumber++;
                    } while (frameNumber <= currentFrame && !stop.IsCancellationRequested);

                    long delay = frameNumber * 1000 / FramesPerSecond - clock.ElapsedMilliseconds;
                    if (delay > 0) stop.WaitHandle.WaitOne((int)delay);
                } while (!stop.IsCancellationRequested);

            Complete:
                video.Complete();
                jpeg.Position = 0;
                preview = new BitmapImage();
                preview.BeginInit();
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.DecodePixelWidth = Math.Min(bounds.Width, 320);
                preview.StreamSource = jpeg;
                preview.EndInit();
                preview.Freeze();
            }
            File.Move(temporary, path);
            return new Result(path, preview);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // A single Motion JPEG video stream in a RIFF AVI container.
    private sealed class MjpegAvi : IDisposable
    {
        private const long MaximumLength = int.MaxValue;
        private readonly BinaryWriter writer;
        private readonly List<(int Offset, int Length)> index = new();
        private readonly long riffSize;
        private readonly long movieSize;
        private readonly long frameCount;
        private readonly long streamLength;
        private readonly long mainBufferSize;
        private readonly long streamBufferSize;
        private readonly long maxBytesPerSecond;
        private int largestFrame;
        private long Position => writer.BaseStream.Position;

        internal MjpegAvi(string path, Gdi.Size size)
        {
            writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew));
            try
            {
                riffSize = Begin("RIFF");
                FourCc("AVI ");
                long header = Begin("LIST");
                FourCc("hdrl");
                long main = Begin("avih");
                writer.Write(1_000_000 / FramesPerSecond);
                maxBytesPerSecond = Reserve();
                writer.Write(0); // Padding granularity.
                writer.Write(0x10); // AVIF_HASINDEX.
                frameCount = Reserve();
                writer.Write(0); // Initial frames.
                writer.Write(1); // Stream count.
                mainBufferSize = Reserve();
                writer.Write(size.Width);
                writer.Write(size.Height);
                for (int i = 0; i < 4; i++) writer.Write(0);
                End(main);

                long stream = Begin("LIST");
                FourCc("strl");
                long details = Begin("strh");
                FourCc("vids");
                FourCc("MJPG");
                writer.Write(0); // Flags.
                writer.Write(0); // Priority and language.
                writer.Write(0); // Initial frames.
                writer.Write(1); // Time scale.
                writer.Write(FramesPerSecond);
                writer.Write(0); // Start.
                streamLength = Reserve();
                streamBufferSize = Reserve();
                writer.Write(-1); // Default quality.
                writer.Write(0); // Variable sample size.
                writer.Write(0); // Left and top.
                writer.Write((short)size.Width);
                writer.Write((short)size.Height);
                End(details);

                long format = Begin("strf");
                writer.Write(40); // BITMAPINFOHEADER size.
                writer.Write(size.Width);
                writer.Write(size.Height);
                writer.Write((short)1); // Planes.
                writer.Write((short)24);
                FourCc("MJPG");
                writer.Write(size.Width * size.Height * 3);
                for (int i = 0; i < 4; i++) writer.Write(0);
                End(format);
                End(stream);
                End(header);

                movieSize = Begin("LIST");
                FourCc("movi");
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        internal bool TryWriteFrame(ReadOnlySpan<byte> jpeg)
        {
            long finalLength = Position + 8L + jpeg.Length + (jpeg.Length & 1) + 8L + 16L * (index.Count + 1);
            if (finalLength > MaximumLength) return false;
            int offset = (int)(Position - (movieSize + sizeof(int)));
            FourCc("00dc");
            writer.Write(jpeg.Length);
            writer.Write(jpeg);
            if ((jpeg.Length & 1) != 0) writer.Write((byte)0);
            index.Add((offset, jpeg.Length));
            largestFrame = Math.Max(largestFrame, jpeg.Length);
            return true;
        }

        internal void Complete()
        {
            End(movieSize);
            long indexSize = Begin("idx1");
            foreach ((int offset, int length) in index)
            {
                FourCc("00dc");
                writer.Write(0x10); // AVIIF_KEYFRAME.
                writer.Write(offset);
                writer.Write(length);
            }
            End(indexSize);
            End(riffSize);
            Patch(frameCount, index.Count);
            Patch(streamLength, index.Count);
            Patch(mainBufferSize, largestFrame);
            Patch(streamBufferSize, largestFrame);
            Patch(maxBytesPerSecond, (largestFrame + 9) * FramesPerSecond);
        }

        private void FourCc(string code)
        {
            foreach (char character in code) writer.Write((byte)character);
        }

        private long Begin(string code)
        {
            FourCc(code);
            return Reserve();
        }

        private long Reserve()
        {
            long offset = Position;
            writer.Write(0);
            return offset;
        }

        private void End(long offset) => Patch(offset, (int)(Position - offset - sizeof(int)));

        private void Patch(long offset, int value)
        {
            long end = Position;
            writer.BaseStream.Position = offset;
            writer.Write(value);
            writer.BaseStream.Position = end;
        }

        public void Dispose() => writer.Dispose();
    }
}
