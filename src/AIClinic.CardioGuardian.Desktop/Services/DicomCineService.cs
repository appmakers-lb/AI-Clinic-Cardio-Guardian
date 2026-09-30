using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Media.Imaging;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Desktop.Services;

public sealed class DicomCineService
{
    private sealed record FrameLocator(string Path, int LocalFrame);

    private readonly List<FrameLocator> _frames = new();
    private readonly Dictionary<string, DicomImage> _images = new(StringComparer.OrdinalIgnoreCase);

    public ImagingSeriesInfo? CurrentSeries { get; private set; }
    public int FrameCount => _frames.Count;
    public int CurrentFrameIndex { get; private set; }
    public double FramesPerSecond => CurrentSeries?.EstimatedFramesPerSecond is > 0
        ? CurrentSeries.EstimatedFramesPerSecond
        : 15;

    public void Reset()
    {
        _frames.Clear();
        _images.Clear();
        CurrentSeries = null;
        CurrentFrameIndex = 0;
    }

    public void LoadSeries(ImagingSeriesInfo series)
    {
        _frames.Clear();
        _images.Clear();
        CurrentSeries = series;
        CurrentFrameIndex = 0;

        foreach (var path in series.FilePaths)
        {
            var dicom = DicomFile.Open(path);
            var frameCount = Math.Max(1, SafeFrameCount(dicom.Dataset));

            for (var frame = 0; frame < frameCount; frame++)
                _frames.Add(new FrameLocator(path, frame));
        }

        if (_frames.Count == 0)
            throw new InvalidDataException("The selected DICOM series contains no renderable frames.");
    }

    public BitmapSource RenderFrame(int frameIndex)
    {
        if (CurrentSeries is null || _frames.Count == 0)
            throw new InvalidOperationException("No DICOM series is loaded.");

        frameIndex = Math.Clamp(frameIndex, 0, _frames.Count - 1);
        CurrentFrameIndex = frameIndex;

        var locator = _frames[frameIndex];
        if (!_images.TryGetValue(locator.Path, out var image))
        {
            image = new DicomImage(locator.Path);
            _images[locator.Path] = image;
        }

        var rendered = image.RenderImage(locator.LocalFrame);
        using var bitmap = rendered.As<Bitmap>();
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var result = new BitmapImage();
        result.BeginInit();
        result.CacheOption = BitmapCacheOption.OnLoad;
        result.StreamSource = stream;
        result.EndInit();
        result.Freeze();
        return result;
    }

    public int NextFrame()
    {
        if (_frames.Count == 0) return 0;
        CurrentFrameIndex = (CurrentFrameIndex + 1) % _frames.Count;
        return CurrentFrameIndex;
    }

    public int PreviousFrame()
    {
        if (_frames.Count == 0) return 0;
        CurrentFrameIndex = CurrentFrameIndex <= 0 ? _frames.Count - 1 : CurrentFrameIndex - 1;
        return CurrentFrameIndex;
    }

    private static int SafeFrameCount(DicomDataset dataset)
    {
        try
        {
            var raw = dataset.GetString(DicomTag.NumberOfFrames);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
        }
        catch
        {
            return 1;
        }
    }
}
