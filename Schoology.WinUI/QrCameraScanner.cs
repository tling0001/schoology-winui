using Microsoft.UI.Xaml;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;

namespace Schoology.WinUI;

/// <summary>Owns the native Windows camera and continuously decodes Schoology QR frames.</summary>
internal sealed class QrCameraScanner : IAsyncDisposable
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private int _decoding;
    private int _completed;

    public event EventHandler<string>? QrCodeDetected;

    public async Task<MediaPlayer> StartAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        MediaFrameSourceGroup? selectedGroup = null;
        MediaFrameSourceInfo? selectedSource = null;
        foreach (var group in groups)
        {
            selectedSource = group.SourceInfos.FirstOrDefault(source => source.SourceKind == MediaFrameSourceKind.Color);
            if (selectedSource is not null)
            {
                selectedGroup = group;
                break;
            }
        }
        if (selectedGroup is null || selectedSource is null)
            throw new InvalidOperationException("No camera with a color stream is available.");

        _capture = new MediaCapture();
        await _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = selectedGroup,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu
        });
        var source = _capture.FrameSources.Values.FirstOrDefault(frame => frame.Info.Id == selectedSource.Id)
            ?? throw new InvalidOperationException("The selected camera stream is unavailable.");

        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        _reader.FrameArrived += OnFrameArrived;
        var result = await _reader.StartAsync();
        if (result != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException($"The camera could not start ({result}).");

        return new MediaPlayer
        {
            Source = MediaSource.CreateFromMediaFrameSource(source),
            AutoPlay = true
        };
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.CompareExchange(ref _decoding, 1, 0) != 0 || Volatile.Read(ref _completed) != 0)
            return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var original = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (original is null) return;
            using var bitmap = SoftwareBitmap.Convert(original, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var buffer = new Windows.Storage.Streams.Buffer(checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4)));
            bitmap.CopyToBuffer(buffer);
            using var data = DataReader.FromBuffer(buffer);
            var pixels = new byte[checked((int)buffer.Length)];
            data.ReadBytes(pixels);
            var luminance = new RGBLuminanceSource(
                pixels, bitmap.PixelWidth, bitmap.PixelHeight,
                RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE }
                }
            };
            var result = reader.Decode(luminance);
            if (!string.IsNullOrWhiteSpace(result?.Text) &&
                Interlocked.Exchange(ref _completed, 1) == 0)
                QrCodeDetected?.Invoke(this, result.Text);
        }
        catch
        {
            // Transient camera frames can be incomplete while the sensor adjusts exposure.
        }
        finally { Volatile.Write(ref _decoding, 0); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            try { await _reader.StopAsync(); } catch { }
            _reader.Dispose();
            _reader = null;
        }
        if (_capture is not null)
        {
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
    }
}
