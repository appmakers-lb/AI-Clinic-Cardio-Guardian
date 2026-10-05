using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using AIClinic.CardioGuardian.Core.Models;
using AIClinic.CardioGuardian.Core.Services;
using AIClinic.CardioGuardian.Desktop.Services;

namespace AIClinic.CardioGuardian.Desktop;

public partial class MainWindow : Window
{
    private enum ViewerMode { None, Video, Dicom }

    private CaseState _case = new();
    private readonly GuardianPolicy _guardian = new();
    private readonly StructuredFindingService _structuredFindingService = new();
    private readonly WholeCaseMemoryService _wholeCaseMemory = new();
    private readonly CoverageEngine _coverageEngine = new();
    private readonly GuardianAlertTracker _alertTracker = new();
    private readonly VoiceCommandParser _voiceCommandParser = new();
    private readonly AuditLogService _audit = new();
    private readonly ResearchCineService _video = new();
    private readonly DicomImportService _dicomImport = new();
    private readonly DicomCineService _dicomCine = new();
    private readonly VoiceCopilotService _voice = new();
    private readonly LocalAiGatewayService _localAi = new();
    private readonly DispatcherTimer _playTimer = new();

    private ViewerMode _viewerMode = ViewerMode.None;
    private List<ImagingSeriesInfo> _series = new();
    private DicomImportResult? _lastImport;
    private CoronarySegment? _selectedSegment;
    private GuardianFinding? _selectedFinding;
    private string? _currentSourceId;
    private bool _isPlaying;
    private CancellationTokenSource? _analysisCts;

    public MainWindow()
    {
        InitializeComponent();

        _playTimer.Tick += PlayTimer_Tick;
        _playTimer.Interval = TimeSpan.FromMilliseconds(100);

        AuditPathText.Text = $"Audit: {_audit.LogFilePath}";
        VoiceStatusText.Text = $"Speech recognition: {_voice.RecognitionStatus}";
        ListenButton.IsEnabled = _voice.SpeechRecognitionAvailable;

        _audit.Write("application_started", new { version = "1.2.0", mode = "RESEARCH" });

        RefreshAll();
        AppendAI(
            "Research Mode ready. Import a cardiac DICOM CD/USB to review all cine runs. " +
            "The optional v1.2 research model can flag evidence-linked stenosis candidates for physician review. " +
            "It is not clinically validated and a negative result is never a clearance statement.");
    }


    private async void ConnectLocalAi_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Checking local AI research gateway…";
        var health = await _localAi.CheckHealthAsync();

        if (!health.Reachable)
        {
            ModelStatusText.Text = "Local AI: OFFLINE";
            AnalyzeButton.IsEnabled = false;
            AnalyzeWholeCaseButton.IsEnabled = false;
            StatusText.Text = "Local AI research gateway is not running.";
            MessageBox.Show(
                "The local AI research gateway is not running.\n\n" +
                "To install the optional research/demo model, run ai-research\\setup_stenoz_model.bat once.\n" +
                "Then start ai-research\\run_gateway.bat and connect again.",
                "Local AI offline",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!health.ModelLoaded)
        {
            ModelStatusText.Text = "Local AI: service online / NO MODEL";
            AnalyzeButton.IsEnabled = false;
            AnalyzeWholeCaseButton.IsEnabled = false;
            StatusText.Text = health.Message;
            MessageBox.Show(
                health.Message + "\n\nThe application will not run fake medical analysis.",
                "No research model loaded",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ModelStatusText.Text = "Research AI demo: CONNECTED";
        AnalyzeButton.IsEnabled = true;
        AnalyzeWholeCaseButton.IsEnabled = _series.Count > 0;
        StatusText.Text = health.Message;
        _audit.Write("local_ai_connected", new { health.ModelLoaded, health.Message });
    }

    private async void AnalyzeSelectedCine_Click(object sender, RoutedEventArgs e)
    {
        if (SeriesList.SelectedItem is not ImagingSeriesInfo series)
        {
            MessageBox.Show("Select a DICOM cine series first.", "No series selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_analysisCts is not null)
            return;

        _analysisCts = new CancellationTokenSource();
        CancelAnalysisButton.IsEnabled = true;
        AnalyzeButton.IsEnabled = false;
        AnalyzeWholeCaseButton.IsEnabled = false;
        StatusText.Text = $"Running local research AI on {series.Id}…";

        try
        {
            var package = await _localAi.AnalyzeSeriesAsync(series, _analysisCts.Token);
            var findings = _structuredFindingService.ToGuardianFindings(package);
            var results = _wholeCaseMemory.AddPackage(_case, findings);
            var addedFindings = results
                .Where(x => x.Status == FindingAddStatus.Added)
                .Select(x => x.Finding)
                .ToArray();

            var coverage = _coverageEngine.ApplyStructuredCoverage(_case, package);

            foreach (var finding in addedFindings)
                ProcessGuardianAlert(finding);

            var skipped = results.Count(x => x.Status != FindingAddStatus.Added);
            ModelStatusText.Text = $"Local AI: {package.ModelId} {package.ModelVersion}";
            StatusText.Text =
                $"Research AI: {findings.Count} finding(s), {addedFindings.Length} added, {skipped} duplicate/invalid; " +
                $"coverage {coverage.Applied} applied, {coverage.Rejected} rejected.";

            RefreshFindings();
            RefreshCoverage();

            if (findings.Count == 0)
            {
                AppendAI(
                    "The research model returned no candidate on the sampled frames. " +
                    "This must not be interpreted as no stenosis or a normal study.");
            }

            _audit.Write("local_ai_analysis_completed", new
            {
                series.Id,
                package.ModelId,
                package.ModelVersion,
                returned = findings.Count,
                added = addedFindings.Length,
                skipped,
                coverageApplied = coverage.Applied,
                coverageRejected = coverage.Rejected
            });
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Local research AI analysis cancelled.";
            _audit.Write("local_ai_analysis_cancelled", new { series.Id });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Local AI analysis failed.";
            _audit.Write("local_ai_analysis_failed", new { series.Id, ex.Message });
            MessageBox.Show(ex.Message, "Local AI analysis failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _analysisCts.Dispose();
            _analysisCts = null;
            CancelAnalysisButton.IsEnabled = false;

            var health = await _localAi.CheckHealthAsync();
            AnalyzeButton.IsEnabled = health.Reachable && health.ModelLoaded;
            AnalyzeWholeCaseButton.IsEnabled = health.Reachable && health.ModelLoaded && _series.Count > 0;
        }
    }

    private async void AnalyzeWholeCase_Click(object sender, RoutedEventArgs e)
    {
        if (_series.Count == 0)
        {
            MessageBox.Show("Import a cardiac DICOM study first.", "No case loaded",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_analysisCts is not null)
            return;

        _analysisCts = new CancellationTokenSource();
        CancelAnalysisButton.IsEnabled = true;
        AnalyzeButton.IsEnabled = false;
        AnalyzeWholeCaseButton.IsEnabled = false;

        var totalReturned = 0;
        var totalAdded = 0;
        var totalSkipped = 0;
        var coverageApplied = 0;
        var coverageRejected = 0;
        var failures = new List<string>();
        string? lastModelId = null;
        string? lastModelVersion = null;

        try
        {
            for (var i = 0; i < _series.Count; i++)
            {
                _analysisCts.Token.ThrowIfCancellationRequested();

                var series = _series[i];
                StatusText.Text =
                    $"Whole-case research AI: cine {i + 1}/{_series.Count} — {series.Id}";

                try
                {
                    var package = await _localAi.AnalyzeSeriesAsync(series, _analysisCts.Token);
                    lastModelId = package.ModelId;
                    lastModelVersion = package.ModelVersion;

                    var findings = _structuredFindingService.ToGuardianFindings(package);
                    totalReturned += findings.Count;

                    var results = _wholeCaseMemory.AddPackage(_case, findings);
                    totalAdded += results.Count(x => x.Status == FindingAddStatus.Added);
                    totalSkipped += results.Count(x => x.Status != FindingAddStatus.Added);

                    var coverage = _coverageEngine.ApplyStructuredCoverage(_case, package);
                    coverageApplied += coverage.Applied;
                    coverageRejected += coverage.Rejected;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{series.Id}: {ex.Message}");
                    _audit.Write("whole_case_series_analysis_failed", new { series.Id, ex.Message });
                }
            }

            if (!string.IsNullOrWhiteSpace(lastModelId))
                ModelStatusText.Text = $"Local AI: {lastModelId} {lastModelVersion}";

            RefreshFindings();
            RefreshCoverage();

            StatusText.Text =
                $"Whole-case AI complete: {_series.Count - failures.Count}/{_series.Count} cine(s), " +
                $"{totalReturned} finding(s), {totalAdded} added, {totalSkipped} duplicate/invalid; " +
                $"coverage {coverageApplied} applied, {coverageRejected} rejected.";

            _audit.Write("whole_case_ai_analysis_completed", new
            {
                seriesCount = _series.Count,
                succeeded = _series.Count - failures.Count,
                failed = failures.Count,
                returned = totalReturned,
                added = totalAdded,
                skipped = totalSkipped,
                coverageApplied,
                coverageRejected,
                modelId = lastModelId,
                modelVersion = lastModelVersion
            });

            if (failures.Count > 0)
            {
                MessageBox.Show(
                    $"Whole-case analysis completed with {failures.Count} cine failure(s).\n\n" +
                    string.Join("\n", failures.Take(5)),
                    "Whole-case analysis completed with warnings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text =
                $"Whole-case analysis cancelled. Findings already accepted remain in the research case.";
            _audit.Write("whole_case_ai_analysis_cancelled", new
            {
                returned = totalReturned,
                added = totalAdded,
                skipped = totalSkipped,
                coverageApplied,
                coverageRejected
            });
            RefreshFindings();
            RefreshCoverage();
        }
        finally
        {
            _analysisCts.Dispose();
            _analysisCts = null;
            CancelAnalysisButton.IsEnabled = false;

            var health = await _localAi.CheckHealthAsync();
            AnalyzeButton.IsEnabled = health.Reachable && health.ModelLoaded;
            AnalyzeWholeCaseButton.IsEnabled = health.Reachable && health.ModelLoaded && _series.Count > 0;
        }
    }

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (_analysisCts is null)
            return;

        StatusText.Text = "Cancelling research AI analysis…";
        CancelAnalysisButton.IsEnabled = false;
        _analysisCts.Cancel();
    }

    private async void ImportDicom_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the cardiac CD, USB, or copied DICOM folder"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            StatusText.Text = "Scanning DICOM folder…";
            ImportSummaryText.Text = "Scanning locally. No DICOM files are uploaded to a cloud service.";
            var progress = new Progress<string>(text => StatusText.Text = text);

            var result = await _dicomImport.ScanFolderAsync(dialog.FolderName, progress);
            _lastImport = result;
            _series = result.Series.ToList();
            SeriesList.ItemsSource = _series;

            RegisterImportedRuns(_series);
            RefreshCaseRuns();

            var first = _series.FirstOrDefault();
            if (first is null)
            {
                PatientStudyText.Text = "No DICOM image series were found.";
                ImportSummaryText.Text =
                    $"Scanned {result.FilesScanned} files. Opened {result.DicomFilesOpened} DICOM files, " +
                    $"but no image series could be grouped.";
                StatusText.Text = "No DICOM image series found.";
                return;
            }

            PatientStudyText.Text =
                $"Patient: {first.PatientName.OrFallback("(not present)")}  |  ID: {first.PatientId.OrFallback("(not present)")}\n" +
                $"Study date: {first.StudyDate.OrFallback("(not present)")}\n" +
                $"Study: {first.StudyDescription.OrFallback("(not present)")}\n" +
                $"Series found: {_series.Count}  |  Likely coronary: {_series.Count(x => x.LikelyCoronaryAngiography)}";

            ImportSummaryText.Text =
                $"Scanned {result.FilesScanned} files; opened {result.DicomFilesOpened} DICOM files; " +
                $"grouped {_series.Count} series. Files remain local on this workstation.";

            if (result.Warnings.Count > 0)
                ImportSummaryText.Text += $"\nWarnings: {result.Warnings.Count} (first: {result.Warnings[0]})";

            var preferredIndex = _series.FindIndex(x => x.LikelyCoronaryAngiography);
            SeriesList.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;

            if (SeriesList.SelectedItem is ImagingSeriesInfo selectedSeries)
                LoadDicomSeries(selectedSeries, 0);

            var aiHealth = await _localAi.CheckHealthAsync();
            AnalyzeWholeCaseButton.IsEnabled = aiHealth.Reachable && aiHealth.ModelLoaded && _series.Count > 0;

            StatusText.Text = "DICOM import complete. First likely coronary cine loaded automatically.";
            _audit.Write("dicom_folder_imported", new
            {
                source = System.IO.Path.GetFileName(dialog.FolderName.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
                filesScanned = result.FilesScanned,
                dicomOpened = result.DicomFilesOpened,
                series = result.Series.Count
            });
        }
        catch (Exception ex)
        {
            _audit.Write("dicom_import_failed", new { ex.Message });
            StatusText.Text = "DICOM import failed.";
            MessageBox.Show(
                $"Could not import the DICOM folder.\n\n{ex.Message}",
                "DICOM import failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RegisterImportedRuns(IEnumerable<ImagingSeriesInfo> series)
    {
        foreach (var item in series)
        {
            _case.AddCineRun(new CineRun
            {
                Id = item.Id,
                SourceKind = "DICOM",
                DisplayName = item.DisplayName,
                StudyInstanceUid = item.StudyInstanceUid,
                SeriesInstanceUid = item.SeriesInstanceUid,
                Modality = item.Modality,
                Projection = item.Projection,
                FrameCount = item.TotalFrames,
                FramesPerSecond = item.EstimatedFramesPerSecond,
                SourcePath = item.FilePaths.FirstOrDefault()
            });
        }
    }

    private void LoadSelectedSeries_Click(object sender, RoutedEventArgs e)
    {
        if (SeriesList.SelectedItem is not ImagingSeriesInfo series)
        {
            MessageBox.Show("Select a DICOM cine series first.", "No series selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LoadDicomSeries(series, 0);
    }

    private void LoadDicomSeries(ImagingSeriesInfo series, int frameToShow)
    {
        try
        {
            StopPlayback();

            _dicomCine.LoadSeries(series);
            _viewerMode = ViewerMode.Dicom;
            _currentSourceId = series.Id;

            CinePlayer.Visibility = Visibility.Collapsed;
            DicomFrameImage.Visibility = Visibility.Visible;
            ViewerPlaceholder.Visibility = Visibility.Collapsed;

            Timeline.IsEnabled = true;
            Timeline.Maximum = Math.Max(1, _dicomCine.FrameCount - 1);

            PrevFrameButton.IsEnabled = true;
            NextFrameButton.IsEnabled = true;
            PlayButton.IsEnabled = true;
            StopButton.IsEnabled = true;

            ViewerSourceText.Text = series.DisplayName;
            ProjectionText.Text = series.Projection.OrFallback("Projection metadata not present");

            var fps = Math.Clamp(_dicomCine.FramesPerSecond, 1, 60);
            _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);

            RenderDicomFrame(Math.Clamp(frameToShow, 0, Math.Max(0, _dicomCine.FrameCount - 1)));

            StatusText.Text = $"Loaded DICOM cine: {series.SeriesDescription.OrFallback(series.Id)}";
            _audit.Write("dicom_series_loaded", new
            {
                series.Id,
                series.Modality,
                series.TotalFrames,
                fps,
                series.Projection
            });
        }
        catch (Exception ex)
        {
            _audit.Write("dicom_series_load_failed", new { series.Id, ex.Message });
            MessageBox.Show(
                "The DICOM cine could not be rendered.\n\n" +
                ex.Message +
                "\n\nIf the study uses a compression syntax not handled by the current renderer, a codec adapter will be required.",
                "DICOM render failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RenderDicomFrame(int frame)
    {
        if (_viewerMode != ViewerMode.Dicom || _dicomCine.CurrentSeries is null) return;

        try
        {
            DicomFrameImage.Source = _dicomCine.RenderFrame(frame);
            Timeline.Value = _dicomCine.CurrentFrameIndex;
            FrameStatusText.Text =
                $"Frame {_dicomCine.CurrentFrameIndex + 1}/{Math.Max(1, _dicomCine.FrameCount)}  |  " +
                $"{_dicomCine.FramesPerSecond:0.#} fps";
        }
        catch (Exception ex)
        {
            StopPlayback();
            StatusText.Text = "DICOM frame render failed.";
            _audit.Write("dicom_frame_render_failed", new
            {
                frame,
                source = _currentSourceId,
                ex.Message
            });

            MessageBox.Show(ex.Message, "Frame render failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SeriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeriesList.SelectedItem is not ImagingSeriesInfo series) return;

        StatusText.Text =
            $"Selected {series.Id}: {series.SeriesDescription.OrFallback("DICOM series")} — " +
            $"{series.TotalFrames} frames.";
    }

    private void PreviousCine_Click(object sender, RoutedEventArgs e) => MoveSeriesSelection(-1);
    private void NextCine_Click(object sender, RoutedEventArgs e) => MoveSeriesSelection(1);

    private void MoveSeriesSelection(int delta)
    {
        if (_series.Count == 0) return;

        var index = SeriesList.SelectedIndex;
        if (index < 0) index = 0;
        index = (index + delta + _series.Count) % _series.Count;
        SeriesList.SelectedIndex = index;
        SeriesList.ScrollIntoView(SeriesList.SelectedItem);

        if (SeriesList.SelectedItem is ImagingSeriesInfo series)
            LoadDicomSeries(series, 0);
    }

    private void OpenCine_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open recorded, de-identified angiography cine/video",
            Filter = "Video files|*.mp4;*.avi;*.wmv;*.mov;*.mkv|All files|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            StopPlayback();

            _video.Load(CinePlayer, dialog.FileName);
            _viewerMode = ViewerMode.Video;
            _currentSourceId = $"VID-{DateTime.UtcNow:yyyyMMddHHmmss}";

            DicomFrameImage.Visibility = Visibility.Collapsed;
            CinePlayer.Visibility = Visibility.Visible;
            ViewerPlaceholder.Visibility = Visibility.Collapsed;

            PrevFrameButton.IsEnabled = true;
            NextFrameButton.IsEnabled = true;
            PlayButton.IsEnabled = true;
            StopButton.IsEnabled = true;

            ViewerSourceText.Text = System.IO.Path.GetFileName(dialog.FileName);
            ProjectionText.Text = "Video source — projection metadata not parsed";
            FrameStatusText.Text = "Video";

            _case.AddCineRun(new CineRun
            {
                Id = _currentSourceId,
                SourceKind = "VIDEO",
                DisplayName = System.IO.Path.GetFileName(dialog.FileName),
                FrameCount = 0,
                FramesPerSecond = 0,
                SourcePath = dialog.FileName
            });

            RefreshCaseRuns();

            StatusText.Text = $"Loaded {System.IO.Path.GetFileName(dialog.FileName)} — Research Mode";
            _audit.Write("cine_video_loaded", new { file = System.IO.Path.GetFileName(dialog.FileName), sourceId = _currentSourceId });
        }
        catch (Exception ex)
        {
            _audit.Write("cine_load_failed", new { ex.Message });
            MessageBox.Show(ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CinePlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (CinePlayer.NaturalDuration.HasTimeSpan)
        {
            Timeline.IsEnabled = true;
            Timeline.Maximum = Math.Max(1, CinePlayer.NaturalDuration.TimeSpan.TotalSeconds);
        }
    }

    private void CinePlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        CinePlayer.Position = TimeSpan.Zero;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerMode == ViewerMode.None) return;

        if (_isPlaying)
        {
            PausePlayback();
            return;
        }

        _isPlaying = true;
        PlayButton.Content = "❚❚ Pause";

        if (_viewerMode == ViewerMode.Video)
        {
            CinePlayer.Play();
            _playTimer.Interval = TimeSpan.FromMilliseconds(200);
        }

        _playTimer.Start();
    }

    private void PausePlayback()
    {
        _isPlaying = false;
        _playTimer.Stop();

        if (_viewerMode == ViewerMode.Video)
            CinePlayer.Pause();

        PlayButton.Content = "▶ Play";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();

        if (_viewerMode == ViewerMode.Video)
        {
            CinePlayer.Stop();
            Timeline.Value = 0;
        }
        else if (_viewerMode == ViewerMode.Dicom && _dicomCine.FrameCount > 0)
        {
            RenderDicomFrame(0);
        }
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        _playTimer.Stop();

        if (_viewerMode == ViewerMode.Video)
            CinePlayer.Pause();

        PlayButton.Content = "▶ Play";
    }

    private void PlayTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isPlaying) return;

        if (_viewerMode == ViewerMode.Dicom)
        {
            var next = _dicomCine.NextFrame();
            RenderDicomFrame(next);
        }
        else if (_viewerMode == ViewerMode.Video && CinePlayer.NaturalDuration.HasTimeSpan)
        {
            Timeline.Value = Math.Clamp(
                CinePlayer.Position.TotalSeconds,
                Timeline.Minimum,
                Timeline.Maximum);
            FrameStatusText.Text = $"{CinePlayer.Position:mm\\:ss}";
        }
    }

    private void PrevFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerMode == ViewerMode.Dicom)
            RenderDicomFrame(_dicomCine.PreviousFrame());
        else if (_viewerMode == ViewerMode.Video)
            CinePlayer.Position = CinePlayer.Position - TimeSpan.FromSeconds(1) < TimeSpan.Zero
                ? TimeSpan.Zero
                : CinePlayer.Position - TimeSpan.FromSeconds(1);
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_viewerMode == ViewerMode.Dicom)
            RenderDicomFrame(_dicomCine.NextFrame());
        else if (_viewerMode == ViewerMode.Video)
            CinePlayer.Position += TimeSpan.FromSeconds(1);
    }

    private void AddManualFinding_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentSourceId))
        {
            MessageBox.Show("Load a DICOM cine or video first.", "No evidence source",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var currentFrame = _viewerMode == ViewerMode.Dicom
            ? _dicomCine.CurrentFrameIndex
            : (int)Math.Round(CinePlayer.Position.TotalSeconds);

        var projection = _viewerMode == ViewerMode.Dicom
            ? _dicomCine.CurrentSeries?.Projection
            : null;

        var dialog = new ManualFindingWindow(_currentSourceId, currentFrame, projection)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        try
        {
            _case.AddFinding(dialog.Result);
            _audit.Write("manual_research_finding_added", new
            {
                dialog.Result.Id,
                dialog.Result.Vessel,
                dialog.Result.Segment,
                dialog.Result.FindingType,
                dialog.Result.Confidence
            });
            RefreshFindings();
            StatusText.Text = "Manual research annotation added. It is not an AI finding.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Could not add annotation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportFindings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import versioned structured research-model findings",
            Filter = "JSON files|*.json|All files|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var package = _structuredFindingService.Parse(System.IO.File.ReadAllText(dialog.FileName));
            var findings = _structuredFindingService.ToGuardianFindings(package);
            var results = _wholeCaseMemory.AddPackage(_case, findings);
            var addedFindings = results
                .Where(x => x.Status == FindingAddStatus.Added)
                .Select(x => x.Finding)
                .ToArray();

            foreach (var finding in addedFindings)
                ProcessGuardianAlert(finding);

            var coverage = _coverageEngine.ApplyStructuredCoverage(_case, package);
            var skipped = results.Count(x => x.Status != FindingAddStatus.Added);

            ModelStatusText.Text = $"Structured research output: {package.ModelId} {package.ModelVersion}";
            ImportSummaryText.Text =
                $"Imported {addedFindings.Length} structured finding(s) from {package.ModelId}:{package.ModelVersion}. " +
                $"{skipped} duplicate/invalid. Coverage: {coverage.Applied} applied, {coverage.Rejected} rejected. " +
                "This does not establish clinical validity.";

            RefreshFindings();
            RefreshCoverage();

            _audit.Write("structured_findings_imported", new
            {
                package.ModelId,
                package.ModelVersion,
                added = addedFindings.Length,
                skipped,
                coverageApplied = coverage.Applied,
                coverageRejected = coverage.Rejected
            });
        }
        catch (Exception ex)
        {
            _audit.Write("structured_findings_import_failed", new { ex.Message });
            MessageBox.Show(ex.Message, "Finding import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FindingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedFinding = (FindingList.SelectedItem as ListBoxItem)?.Tag as GuardianFinding;
        UpdateFindingDetail();
    }

    private void UpdateFindingDetail()
    {
        if (_selectedFinding is null)
        {
            FindingDetailText.Text = "Select a finding to see its evidence and source.";
            return;
        }

        FindingDetailText.Text =
            $"{_selectedFinding.Vessel} {_selectedFinding.Segment} — {_selectedFinding.FindingType}\n" +
            $"Confidence: {_selectedFinding.Confidence:P0} | Priority: {_selectedFinding.Priority} | Status: {_selectedFinding.Status}\n" +
            $"Source: {_selectedFinding.Source} / {_selectedFinding.SourceVersion.OrFallback("(not specified)")}\n" +
            $"{_selectedFinding.MeasurementSummary.OrFallback(string.Empty)}\n" +
            $"{_selectedFinding.Explanation.OrFallback("No explanation supplied.")}";
    }

    private void ConfirmFinding_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFinding is null) return;
        _selectedFinding.Confirm();
        _audit.Write("finding_confirmed_by_physician", new { _selectedFinding.Id });
        RefreshFindings();
    }

    private void DismissFinding_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFinding is null) return;
        _selectedFinding.Dismiss();
        _audit.Write("finding_dismissed_by_physician", new { _selectedFinding.Id });
        RefreshFindings();
    }

    private void ShowEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFinding is null)
        {
            MessageBox.Show("Select a structured or manual research finding first.",
                "No finding selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var evidenceText = new StringBuilder();
        evidenceText.AppendLine($"{_selectedFinding.Vessel} {_selectedFinding.Segment} — {_selectedFinding.FindingType}");
        evidenceText.AppendLine($"Confidence: {_selectedFinding.Confidence:P0}");
        evidenceText.AppendLine($"Source: {_selectedFinding.SourceVersion.OrFallback(_selectedFinding.Source.ToString())}");
        evidenceText.AppendLine();

        for (var i = 0; i < _selectedFinding.Evidence.Count; i++)
        {
            var evi = _selectedFinding.Evidence[i];
            evidenceText.AppendLine(
                $"{i + 1}. {evi.SourceId} | frames {evi.FrameStart?.ToString() ?? "?"}-{evi.FrameEnd?.ToString() ?? "?"} | " +
                $"{evi.Projection.OrFallback("projection n/a")} | {evi.Description}");
        }

        NavigateToFirstEvidence(_selectedFinding);

        MessageBox.Show(evidenceText.ToString(), "Evidence bundle — Research Mode",
            MessageBoxButton.OK, MessageBoxImage.Information);

        _audit.Write("evidence_bundle_shown", new { _selectedFinding.Id });
    }

    private void NavigateToFirstEvidence(GuardianFinding finding)
    {
        var evidence = finding.Evidence.FirstOrDefault();
        if (evidence is null) return;

        var series = _series.FirstOrDefault(x =>
            string.Equals(x.Id, evidence.SourceId, StringComparison.OrdinalIgnoreCase));

        if (series is not null)
        {
            SeriesList.SelectedItem = series;
            LoadDicomSeries(series, evidence.FrameStart ?? 0);
            return;
        }

        if (_viewerMode == ViewerMode.Video &&
            string.Equals(_currentSourceId, evidence.SourceId, StringComparison.OrdinalIgnoreCase) &&
            evidence.FrameStart is int seconds)
        {
            CinePlayer.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        }
    }

    private void CoverageList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _selectedSegment = (CoverageList.SelectedItem as ListBoxItem)?.Tag as CoronarySegment;

    private void SetCoverage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSegment is null) return;

        var text = (CoverageChoice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Unassessed";
        if (!Enum.TryParse<CoverageState>(text, out var state)) return;

        _case.SetCoverage(
            _selectedSegment.Vessel,
            _selectedSegment.Segment,
            state,
            "Manual research coverage annotation");

        _audit.Write("coverage_changed", new
        {
            _selectedSegment.Vessel,
            _selectedSegment.Segment,
            state = state.ToString(),
            source = "manual"
        });

        RefreshCoverage();
    }

    private void CoverageSummary_Click(object sender, RoutedEventArgs e)
    {
        var summary = _guardian.CoverageSummary(_case);
        AppendAI(summary);
        SpeakCopilotIfEnabled(summary);
        _audit.Write("coverage_summary_requested");
    }

    private void Ask_Click(object sender, RoutedEventArgs e) => AskQuestion();

    private void QuestionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AskQuestion();
            e.Handled = true;
        }
    }

    private void AskQuestion()
    {
        var q = QuestionBox.Text.Trim();
        if (q.Length == 0) return;

        QuestionBox.Clear();
        AppendDoctor(q);
        var answer = BuildGroundedAnswer(q);
        AppendAI(answer);
        SpeakCopilotIfEnabled(answer);

        _audit.Write("copilot_question", new
        {
            question = q,
            answerType = "grounded_case_state"
        });
    }

    private string BuildGroundedAnswer(string question)
    {
        var lower = question.ToLowerInvariant();

        if (lower.Contains("coverage") ||
            lower.Contains("incomplete") ||
            lower.Contains("unassessed") ||
            lower.Contains("not seen") ||
            lower.Contains("not assessed"))
        {
            return _guardian.CoverageSummary(_case);
        }

        if (lower.Contains("anything else") ||
            lower.Contains("finding") ||
            lower.Contains("findings"))
        {
            return _guardian.FindingSummary(_case);
        }

        if (lower.Contains("why") || lower.Contains("evidence"))
        {
            if (_selectedFinding is null)
                return "Select a finding first. I only explain evidence linked to a structured or manual research finding.";

            return $"{_selectedFinding.Vessel} {_selectedFinding.Segment}: " +
                   $"{_selectedFinding.Explanation.OrFallback("No explanation supplied.")} " +
                   $"Evidence count: {_selectedFinding.Evidence.Count}.";
        }

        if (lower.Contains("block") ||
            lower.Contains("stenosis") ||
            lower.Contains("lesion") ||
            lower.Contains("occlusion") ||
            lower.Contains("cto") ||
            lower.Contains("artery"))
        {
            return _case.ActiveFindings().Count > 0
                ? _guardian.FindingSummary(_case)
                : _guardian.SafeNoModelResponse();
        }

        if (lower.Contains("model"))
        {
            var sources = _case.Findings
                .Where(x => x.Source == FindingSource.StructuredResearchModel)
                .Select(x => x.SourceVersion)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return sources.Length == 0
                ? "No structured medical-model findings are loaded."
                : "Loaded structured research sources: " + string.Join(", ", sources);
        }

        if (lower.Contains("cine") || lower.Contains("series") || lower.Contains("run"))
            return $"This case currently contains {_case.CineRuns.Count} cine run(s).";

        return "I can answer only from this case's imported DICOM metadata, coverage state, and evidence-linked structured findings. " +
               "I will not infer a diagnosis from raw images without a validated medical vision model.";
    }

    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (!_voice.SpeechRecognitionAvailable)
        {
            MessageBox.Show(_voice.RecognitionStatus, "Voice recognition unavailable",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ListenButton.IsEnabled = false;
        VoiceStatusText.Text = "Listening…";

        var commandText = await _voice.ListenForCommandAsync();
        ListenButton.IsEnabled = true;
        VoiceStatusText.Text = $"Speech recognition: {_voice.RecognitionStatus}";

        if (string.IsNullOrWhiteSpace(commandText))
        {
            AppendAI("No voice command recognized.");
            return;
        }

        AppendDoctor($"[voice] {commandText}");
        var command = _voiceCommandParser.Parse(commandText);

        switch (command.Intent)
        {
            case VoiceIntent.NextCine:
                MoveSeriesSelection(1);
                AppendAI("Moved to the next cine.");
                return;

            case VoiceIntent.PreviousCine:
                MoveSeriesSelection(-1);
                AppendAI("Moved to the previous cine.");
                return;

            case VoiceIntent.MuteVoice:
                VoiceEnabled.IsChecked = false;
                AppendAI("Voice output muted.");
                return;

            case VoiceIntent.UnmuteVoice:
                VoiceEnabled.IsChecked = true;
                AppendAI("Voice output enabled.");
                return;

            case VoiceIntent.ShowEvidence:
                if (_selectedFinding is null)
                {
                    AppendAI("Select a finding first. Evidence is only shown for an evidence-linked research finding.");
                    return;
                }
                ShowEvidence_Click(this, new RoutedEventArgs());
                return;

            case VoiceIntent.CoverageSummary:
            {
                var summary = _guardian.CoverageSummary(_case);
                AppendAI(summary);
                SpeakCopilotIfEnabled(summary);
                return;
            }

            case VoiceIntent.FindingsSummary:
            {
                var summary = _guardian.FindingSummary(_case);
                AppendAI(summary);
                SpeakCopilotIfEnabled(summary);
                return;
            }

            case VoiceIntent.Play:
                if (!_isPlaying && _viewerMode != ViewerMode.None)
                    PlayPause_Click(this, new RoutedEventArgs());
                return;

            case VoiceIntent.Pause:
                if (_isPlaying)
                    PausePlayback();
                return;

            case VoiceIntent.Stop:
                Stop_Click(this, new RoutedEventArgs());
                return;

            case VoiceIntent.Unknown:
            default:
                var answer = BuildGroundedAnswer(commandText);
                AppendAI(answer);
                SpeakCopilotIfEnabled(answer);
                return;
        }
    }

    private void ProcessGuardianAlert(GuardianFinding finding)
    {
        var decision = _guardian.EvaluateAlert(
            finding,
            VoiceEnabled.IsChecked == true,
            _selectedSegment?.Vessel);

        if (!decision.ShouldAlert)
        {
            _audit.Write("guardian_alert_not_issued", new
            {
                finding.Id,
                decision.Reason,
                decision.IsCrossVessel
            });
            return;
        }

        if (!_alertTracker.TryAcquire(finding, DateTime.UtcNow, out var trackerReason))
        {
            _audit.Write("guardian_alert_suppressed", new
            {
                finding.Id,
                reason = trackerReason,
                decision.IsCrossVessel
            });
            return;
        }

        _audit.Write("guardian_alert_issued", new
        {
            finding.Id,
            decision.Reason,
            decision.IsCrossVessel,
            finding.Priority,
            finding.Confidence,
            evidenceCount = finding.Evidence.Count
        });

        var frame = finding.Evidence.FirstOrDefault()?.FrameStart;
        var frameText = frame.HasValue ? $" at frame {frame.Value + 1}" : string.Empty;

        _voice.Speak(
            $"Doctor, research-only AI flagged a stenosis candidate{frameText}. " +
            "Please review the evidence. This is not a diagnosis.");
    }

    private void VoiceEnabled_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = VoiceEnabled.IsChecked == true;
        _audit.Write("voice_output_changed", new { enabled });

        if (enabled && _voice.SpeechSynthesisAvailable)
            _voice.Speak("Cardio voice enabled.");
    }

    private void SpeakCopilotIfEnabled(string text)
    {
        if (VoiceEnabled.IsChecked == true)
            _voice.Speak(text);
    }

    private void ClearCase_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Clear the current research case from the application session? This does not delete the source CD/USB files.",
                "Clear case",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        StopPlayback();
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = null;
        CancelAnalysisButton.IsEnabled = false;
        _alertTracker.Reset();

        _case = new CaseState();
        _series.Clear();
        _lastImport = null;
        _selectedSegment = null;
        _selectedFinding = null;
        _currentSourceId = null;
        _viewerMode = ViewerMode.None;
        _dicomCine.Reset();

        SeriesList.ItemsSource = null;
        CaseRunsList.Items.Clear();
        FindingList.Items.Clear();
        DicomFrameImage.Source = null;
        DicomFrameImage.Visibility = Visibility.Collapsed;
        CinePlayer.Visibility = Visibility.Collapsed;
        CinePlayer.Source = null;
        ViewerPlaceholder.Visibility = Visibility.Visible;

        PatientStudyText.Text = "No DICOM study loaded.";
        ImportSummaryText.Text = string.Empty;
        ViewerSourceText.Text = "No source loaded";
        ProjectionText.Text = string.Empty;
        FrameStatusText.Text = string.Empty;
        Timeline.Value = 0;
        Timeline.IsEnabled = false;
        PrevFrameButton.IsEnabled = false;
        NextFrameButton.IsEnabled = false;
        PlayButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        ModelStatusText.Text = "Medical vision model: NOT CONNECTED";
        AnalyzeButton.IsEnabled = false;
        AnalyzeWholeCaseButton.IsEnabled = false;

        RefreshAll();
        _audit.Write("case_cleared");
    }

    private void RefreshAll()
    {
        RefreshCoverage();
        RefreshFindings();
        RefreshCaseRuns();
    }

    private void RefreshCoverage()
    {
        CoverageList.Items.Clear();

        foreach (var seg in _case.Segments)
        {
            var marker = seg.Coverage switch
            {
                CoverageState.Adequate => "✓",
                CoverageState.Partial => "~",
                CoverageState.Incomplete => "!",
                _ => "?"
            };

            CoverageList.Items.Add(new ListBoxItem
            {
                Content = $"{marker}  {seg.Vessel} {seg.Segment} — {seg.Coverage}",
                Tag = seg
            });
        }

        CoverageSummaryText.Text = _guardian.CoverageSummary(_case);
    }

    private void RefreshFindings()
    {
        var selectedId = _selectedFinding?.Id;
        FindingList.Items.Clear();

        foreach (var finding in _case.Findings)
        {
            var marker = finding.Priority switch
            {
                FindingPriority.HighPriorityReview => "⚠",
                FindingPriority.Review => "!",
                FindingPriority.Information => "i",
                _ => "·"
            };

            var sourceMarker = finding.Source == FindingSource.ManualResearch ? "MANUAL" : "MODEL";
            var item = new ListBoxItem
            {
                Content =
                    $"{marker} {finding.Vessel} {finding.Segment} — {HumanizeFindingType(finding.FindingType)} " +
                    $"({finding.Confidence:P0}) [{sourceMarker}] [{finding.Status}]",
                Tag = finding
            };

            FindingList.Items.Add(item);

            if (string.Equals(finding.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                FindingList.SelectedItem = item;
        }

        if (_case.Findings.Count == 0)
        {
            FindingDetailText.Text =
                "No structured findings loaded. Import versioned AI findings JSON or add a manual research annotation.";
        }

        UpdateFindingDetail();
    }

    private void RefreshCaseRuns()
    {
        CaseRunsList.Items.Clear();
        foreach (var run in _case.CineRuns)
        {
            CaseRunsList.Items.Add(
                $"{run.Id} | {run.SourceKind} | {run.DisplayName}");
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _playTimer.Stop();
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _voice.Dispose();
        _localAi.Dispose();
        _audit.Write("application_closing");
    }

    private void AppendDoctor(string text)
    {
        ChatLog.AppendText($"Doctor: {text}{Environment.NewLine}");
        ChatLog.ScrollToEnd();
    }

    private void AppendAI(string text)
    {
        ChatLog.AppendText($"Cardio: {text}{Environment.NewLine}{Environment.NewLine}");
        ChatLog.ScrollToEnd();
    }

    private static string HumanizeFindingType(string type) =>
        type.Replace("_", " ", StringComparison.Ordinal).Trim();
}

internal static class UiStringExtensions
{
    public static string OrFallback(this string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
