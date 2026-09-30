using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FellowOakDicom;
using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Desktop.Services;

public sealed class DicomImportService
{
    private sealed record InstanceMeta(
        string Path,
        string StudyUid,
        string SeriesUid,
        string PatientName,
        string PatientId,
        string StudyDate,
        string StudyDescription,
        string SeriesDescription,
        string Modality,
        string Projection,
        int InstanceNumber,
        int Frames,
        double FramesPerSecond);

    public async Task<DicomImportResult> ScanFolderAsync(
        string folder,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(folder);

        return await Task.Run(() =>
        {
            var instances = new List<InstanceMeta>();
            var warnings = new List<string>();
            var files = EnumerateCandidateFiles(folder).ToList();
            var opened = 0;
            var skipped = 0;

            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = files[i];

                if (i % 25 == 0)
                    progress?.Report($"Scanning DICOM files: {i}/{files.Count}");

                try
                {
                    var dicom = DicomFile.Open(path);
                    var ds = dicom.Dataset;

                    var studyUid = SafeString(ds, DicomTag.StudyInstanceUID);
                    var seriesUid = SafeString(ds, DicomTag.SeriesInstanceUID);

                    // DICOMDIR and non-image administrative objects do not belong in a cine series.
                    if (string.IsNullOrWhiteSpace(studyUid) || string.IsNullOrWhiteSpace(seriesUid))
                    {
                        skipped++;
                        continue;
                    }

                    opened++;

                    var modality = SafeString(ds, DicomTag.Modality);
                    var description = SafeString(ds, DicomTag.SeriesDescription);
                    var studyDescription = SafeString(ds, DicomTag.StudyDescription);
                    var frameCount = Math.Max(1, SafeInt(ds, DicomTag.NumberOfFrames, 1));
                    var instanceNumber = SafeInt(ds, DicomTag.InstanceNumber, i + 1);
                    var fps = EstimateFramesPerSecond(ds);
                    var projection = BuildProjection(ds);

                    instances.Add(new InstanceMeta(
                        path,
                        studyUid,
                        seriesUid,
                        SafeString(ds, DicomTag.PatientName),
                        SafeString(ds, DicomTag.PatientID),
                        SafeString(ds, DicomTag.StudyDate),
                        studyDescription,
                        description,
                        modality,
                        projection,
                        instanceNumber,
                        frameCount,
                        fps));
                }
                catch (Exception ex)
                {
                    skipped++;
                    if (warnings.Count < 20)
                        warnings.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
                }
            }

            var series = instances
                .GroupBy(x => new { x.StudyUid, x.SeriesUid })
                .Select(group =>
                {
                    var ordered = group.OrderBy(x => x.InstanceNumber).ToList();
                    var first = ordered[0];
                    var totalFrames = ordered.Sum(x => x.Frames);
                    var fps = ordered.Select(x => x.FramesPerSecond).FirstOrDefault(x => x > 0);
                    var combinedText = $"{first.SeriesDescription} {first.StudyDescription} {first.Modality}".ToLowerInvariant();

                    return new ImagingSeriesInfo
                    {
                        Id = StableSeriesId(first.SeriesUid),
                        StudyInstanceUid = first.StudyUid,
                        SeriesInstanceUid = first.SeriesUid,
                        PatientName = first.PatientName,
                        PatientId = first.PatientId,
                        StudyDate = first.StudyDate,
                        StudyDescription = first.StudyDescription,
                        SeriesDescription = first.SeriesDescription,
                        Modality = first.Modality,
                        Projection = first.Projection,
                        InstanceCount = ordered.Count,
                        TotalFrames = totalFrames,
                        EstimatedFramesPerSecond = fps > 0 ? fps : 15,
                        LikelyCoronaryAngiography =
                            string.Equals(first.Modality, "XA", StringComparison.OrdinalIgnoreCase) ||
                            combinedText.Contains("coron") ||
                            combinedText.Contains("cath") ||
                            combinedText.Contains("angio"),
                        FilePaths = ordered.Select(x => x.Path).ToArray()
                    };
                })
                .OrderByDescending(x => x.LikelyCoronaryAngiography)
                .ThenBy(x => x.StudyDate)
                .ThenBy(x => x.SeriesDescription)
                .ToArray();

            progress?.Report($"DICOM scan complete: {series.Length} series.");

            return new DicomImportResult
            {
                SourceFolder = folder,
                FilesScanned = files.Count,
                DicomFilesOpened = opened,
                FilesSkipped = skipped,
                Series = series,
                Warnings = warnings
            };
        }, cancellationToken);
    }


    private static string StableSeriesId(string seriesUid)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seriesUid));
        return "SER-" + Convert.ToHexString(hash)[..12];
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly);
        }

        foreach (var file in files)
        {
            var name = System.IO.Path.GetFileName(file);
            if (name.Equals("DICOMDIR", StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetExtension(file).Equals(".dcm", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(file)) ||
                IsLikelyDicomByExtension(file))
            {
                yield return file;
            }
        }
    }

    private static bool IsLikelyDicomByExtension(string file)
    {
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        return ext is ".dicom" or ".ima" or ".dic" or ".img";
    }

    private static string SafeString(DicomDataset dataset, DicomTag tag)
    {
        try { return dataset.GetString(tag)?.Trim() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int SafeInt(DicomDataset dataset, DicomTag tag, int fallback)
    {
        var text = SafeString(dataset, tag);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static double SafeDouble(DicomDataset dataset, DicomTag tag)
    {
        var text = SafeString(dataset, tag);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static double EstimateFramesPerSecond(DicomDataset dataset)
    {
        var cineRate = SafeDouble(dataset, DicomTag.CineRate);
        if (cineRate > 0) return cineRate;

        var recommended = SafeDouble(dataset, DicomTag.RecommendedDisplayFrameRate);
        if (recommended > 0) return recommended;

        var frameTimeMs = SafeDouble(dataset, DicomTag.FrameTime);
        if (frameTimeMs > 0) return 1000.0 / frameTimeMs;

        return 0;
    }

    private static string BuildProjection(DicomDataset dataset)
    {
        var primary = SafeString(dataset, DicomTag.PositionerPrimaryAngle);
        var secondary = SafeString(dataset, DicomTag.PositionerSecondaryAngle);

        if (string.IsNullOrWhiteSpace(primary) && string.IsNullOrWhiteSpace(secondary))
            return string.Empty;

        return $"Primary {primary.OrDash()}°, Secondary {secondary.OrDash()}°";
    }
}

internal static class DicomTextExtensions
{
    public static string OrDash(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? "?" : value;
}
