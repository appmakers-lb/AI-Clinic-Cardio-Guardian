using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
    private bool _findingFocusMode;
    private const double FindingFocusScale = 3.0;
    private CancellationTokenSource? _analysisCts;

    public MainWindow()
    {
        InitializeComponent();

        _playTimer.Tick += PlayTimer_Tick;
        _playTimer.Interval = TimeSpan.FromMilliseconds(100);
        OverlayCanvas.SizeChanged += (_, _) => DrawSelectedFindingOverlay();
        ImageSurface.MouseLeftButtonDown += ImageSurface_MouseLeftButtonDown;

        AuditPathText.Text = $"Audit: {_audit.LogFilePath}";
        VoiceStatusText.Text = $"Speech recognition: {_voice.RecognitionStatus}";
        ListenButton.IsEnabled = _voice.SpeechRecognitionAvailable;

        _audit.Write("application_started", new { version = "1.4.0", mode = "RESEARCH" });

        RefreshAll();
        AppendAI(
            "Research Mode ready. Import a cardiac DICOM CD/USB to review all cine runs. " +
            "The optional v1.4 research model only surfaces stenosis candidates that also have vessel support " +
            "and temporal persistence. When geometry quality is sufficient it shows a wide-range apparent diameter " +
            "reduction estimate. It is not clinical QCA and a negative result is never a clearance statement.");
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
                $"Research AI: {findings.Count} QCA-qualified finding(s), {addedFindings.Length} added, {skipped} duplicate/invalid; " +
                $"coverage {coverage.Applied} applied, {coverage.Rejected} rejected.";

            RefreshFindings();
            RefreshCoverage();

            if (findings.Count == 0)
            {
                var abstention = package.AnalysisNote.OrFallback(
                    "No candidate passed the multi-frame vessel/QCA quality gates.");
                AppendAI(
                    abstention + " This must not be interpreted as no stenosis or a normal study.");
                StatusText.Text = "No QCA-qualified finding reported — review analysis note/evidence quality.";
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

        try
        {
            StatusText.Text =
                $"Whole-case research AI: analyzing {_series.Count} cine series and linking compatible projections…";

            var package = await _localAi.AnalyzeCaseAsync(_series, _analysisCts.Token);
            ModelStatusText.Text = $"Local AI: {package.ModelId} {package.ModelVersion}";

            var findings = _structuredFindingService.ToGuardianFindings(package);
            var results = _wholeCaseMemory.AddPackage(_case, findings);
            var added = results.Count(x => x.Status == FindingAddStatus.Added);
            var skipped = results.Count(x => x.Status != FindingAddStatus.Added);

            var coverage = _coverageEngine.ApplyStructuredCoverage(_case, package);

            RefreshFindings();
            RefreshCoverage();

            var multiView = findings.Count(x => x.MultiViewConfirmed);
            StatusText.Text =
                $"Whole-case AI complete: {findings.Count} finding(s), {added} added, {skipped} duplicate/invalid; " +
                $"{multiView} multi-view linked; coverage {coverage.Applied} applied, {coverage.Rejected} rejected.";

            if (!string.IsNullOrWhiteSpace(package.AnalysisNote))
                AppendAI(package.AnalysisNote);

            if (findings.Count == 0)
            {
                AppendAI(
                    "No finding passed the frame-quality, vessel, QCA/occlusion, and multi-frame gates. " +
                    "This is not evidence of a normal study and is not clinical clearance.");
            }

            _audit.Write("whole_case_ai_analysis_completed", new
            {
                seriesCount = _series.Count,
                returned = findings.Count,
                added,
                skipped,
                multiViewLinked = multiView,
                coverageApplied = coverage.Applied,
                coverageRejected = coverage.Rejected,
                package.ModelId,
                package.ModelVersion
            });
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Whole-case analysis cancelled.";
            _audit.Write("whole_case_ai_analysis_cancelled");
            RefreshFindings();
            RefreshCoverage();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Whole-case AI analysis failed.";
            _audit.Write("whole_case_ai_analysis_failed", new { ex.Message });
            MessageBox.Show(
                ex.Message,
                "Whole-case AI analysis failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _analysisCts.Dispose();
            _analysisCts = null;
            CancelAnalysisButton.IsEnabled = false;

            var health = await _localAi.CheckHealthAsync();
            AnalyzeButton.IsEnabled = health.Reachable && health.ModelLoaded;
            AnalyzeWholeCaseButton.IsEnabled =
                health.Reachable && health.ModelLoaded && _series.Count > 0;
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
            ResetFindingFocusMode(false);
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
            DrawSelectedFindingOverlay();
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
            ResetFindingFocusMode(false);
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
        ResetFindingFocusMode(false);
        _selectedFinding = (FindingList.SelectedItem as ListBoxItem)?.Tag as GuardianFinding;
        UpdateFindingDetail();
        DrawSelectedFindingOverlay();
    }

    private void FindingList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selectedFinding is null)
            return;

        NavigateToFirstEvidence(_selectedFinding);
        Dispatcher.BeginInvoke(new Action(EnterFindingFocusMode), DispatcherPriority.Loaded);
        e.Handled = true;
    }

    private void UpdateFindingDetail()
    {
        if (_selectedFinding is null)
        {
            FindingDetailText.Text = "Select a finding to see its evidence and source.";
            return;
        }

        var scoreLabel = _selectedFinding.Source == FindingSource.StructuredResearchModel
            ? $"Model score: {_selectedFinding.Confidence:0.00}"
            : $"Confidence: {_selectedFinding.Confidence:P0}";

        var estimateLabel = BuildResearchEstimateLabel(_selectedFinding);

        FindingDetailText.Text =
            $"{_selectedFinding.Vessel} {_selectedFinding.Segment} — {_selectedFinding.FindingType}\n" +
            $"{scoreLabel} | Priority: {_selectedFinding.Priority} | Status: {_selectedFinding.Status}\n" +
            $"{estimateLabel}\n" +
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
        evidenceText.AppendLine(
            _selectedFinding.Source == FindingSource.StructuredResearchModel
                ? $"Model score: {_selectedFinding.Confidence:0.00} (not disease probability)"
                : $"Confidence: {_selectedFinding.Confidence:P0}");
        evidenceText.AppendLine($"Source: {_selectedFinding.SourceVersion.OrFallback(_selectedFinding.Source.ToString())}");
        evidenceText.AppendLine();

        for (var i = 0; i < _selectedFinding.Evidence.Count; i++)
        {
            var evi = _selectedFinding.Evidence[i];
            var startFrame = evi.FrameStart.HasValue ? (evi.FrameStart.Value + 1).ToString() : "?";
            var endFrame = evi.FrameEnd.HasValue ? (evi.FrameEnd.Value + 1).ToString() : "?";
            evidenceText.AppendLine(
                $"{i + 1}. {evi.SourceId} | displayed frame {startFrame}-{endFrame} | " +
                $"{evi.Projection.OrFallback("projection n/a")} | {evi.Description}");
        }

        NavigateToFirstEvidence(_selectedFinding);

        FindingDetailText.Text =
            $"{_selectedFinding.Vessel} {_selectedFinding.Segment} — {_selectedFinding.FindingType}\n" +
            $"Model score: {_selectedFinding.Confidence:0.00} (not disease probability)\n" +
            $"{BuildResearchEstimateLabel(_selectedFinding)}\n" +
            $"Evidence: {string.Join("; ", _selectedFinding.Evidence.Select(e => $"frame {(e.FrameStart ?? 0) + 1} — {e.Description.OrFallback("research candidate")}"))}";

        StatusText.Text = "Evidence frame loaded and AI candidate region highlighted.";
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

    private void DrawSelectedFindingOverlay()
    {
        OverlayCanvas.Children.Clear();

        if (!TryGetSelectedOverlayGeometry(
                out _,
                out var centerX,
                out var centerY,
                out var baseRadius,
                out var canvasWidth,
                out var canvasHeight))
        {
            return;
        }

        var compensation = _findingFocusMode ? 1.0 / FindingFocusScale : 1.0;
        var radius = Math.Max(8, baseRadius * compensation);
        var strokeWidth = Math.Max(1.2, 4 * compensation);
        var crossWidth = Math.Max(1.0, 2 * compensation);

        var ring = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = Brushes.OrangeRed,
            StrokeThickness = strokeWidth,
            Fill = Brushes.Transparent
        };

        Canvas.SetLeft(ring, centerX - radius);
        Canvas.SetTop(ring, centerY - radius);
        OverlayCanvas.Children.Add(ring);

        var horizontal = new Line
        {
            X1 = centerX - radius * 0.55,
            X2 = centerX + radius * 0.55,
            Y1 = centerY,
            Y2 = centerY,
            Stroke = Brushes.OrangeRed,
            StrokeThickness = crossWidth
        };
        var vertical = new Line
        {
            X1 = centerX,
            X2 = centerX,
            Y1 = centerY - radius * 0.55,
            Y2 = centerY + radius * 0.55,
            Stroke = Brushes.OrangeRed,
            StrokeThickness = crossWidth
        };
        OverlayCanvas.Children.Add(horizontal);
        OverlayCanvas.Children.Add(vertical);

        var isTotalOcclusion = string.Equals(
            _selectedFinding!.FindingType,
            "SuspectedTotalOcclusion",
            StringComparison.OrdinalIgnoreCase);

        var labelText = isTotalOcclusion
            ? (_findingFocusMode
                ? "FOCUS • POSSIBLE TOTAL OCCLUSION • Esc to exit"
                : "RESEARCH • POSSIBLE TOTAL OCCLUSION")
            : _selectedFinding.EstimatedDiameterStenosisPercent is double estimate
                ? (_findingFocusMode
                    ? $"FOCUS • QCA ~{estimate:0}% • Esc to exit"
                    : $"RESEARCH QCA • ~{estimate:0}% diameter stenosis")
                : (_findingFocusMode
                    ? $"FOCUS • candidate score {_selectedFinding.Confidence:0.00} • Esc to exit"
                    : $"RESEARCH AI CANDIDATE • score {_selectedFinding.Confidence:0.00}");

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 15, 23, 42)),
            BorderBrush = Brushes.OrangeRed,
            BorderThickness = new Thickness(Math.Max(0.5, compensation)),
            CornerRadius = new CornerRadius(Math.Max(1.5, 4 * compensation)),
            Padding = new Thickness(6 * compensation, 3 * compensation, 6 * compensation, 3 * compensation),
            Child = new TextBlock
            {
                Text = labelText,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = Math.Max(4.5, 11 * compensation)
            }
        };

        Canvas.SetLeft(label, Math.Clamp(
            centerX + radius + (6 * compensation),
            4 * compensation,
            Math.Max(4 * compensation, canvasWidth - (240 * compensation))));
        Canvas.SetTop(label, Math.Clamp(
            centerY - (14 * compensation),
            4 * compensation,
            Math.Max(4 * compensation, canvasHeight - (32 * compensation))));
        OverlayCanvas.Children.Add(label);
    }

    private bool TryGetSelectedOverlayGeometry(
        out EvidenceReference evidence,
        out double centerX,
        out double centerY,
        out double radius,
        out double canvasWidth,
        out double canvasHeight)
    {
        evidence = null!;
        centerX = centerY = radius = canvasWidth = canvasHeight = 0;

        if (_viewerMode != ViewerMode.Dicom ||
            _selectedFinding is null ||
            DicomFrameImage.Source is not BitmapSource bitmap ||
            string.IsNullOrWhiteSpace(_currentSourceId))
        {
            return false;
        }

        var frame = _dicomCine.CurrentFrameIndex;
        var match = _selectedFinding.Evidence.FirstOrDefault(x =>
            string.Equals(x.SourceId, _currentSourceId, StringComparison.OrdinalIgnoreCase) &&
            x.FrameStart.HasValue &&
            x.FrameEnd.HasValue &&
            frame >= x.FrameStart.Value &&
            frame <= x.FrameEnd.Value &&
            x.NormalizedCenterX.HasValue &&
            x.NormalizedCenterY.HasValue);

        if (match is null)
            return false;

        canvasWidth = OverlayCanvas.ActualWidth;
        canvasHeight = OverlayCanvas.ActualHeight;
        if (canvasWidth <= 1 || canvasHeight <= 1 || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            return false;

        var imageScale = Math.Min(
            canvasWidth / bitmap.PixelWidth,
            canvasHeight / bitmap.PixelHeight);

        var displayedWidth = bitmap.PixelWidth * imageScale;
        var displayedHeight = bitmap.PixelHeight * imageScale;
        var offsetX = (canvasWidth - displayedWidth) / 2.0;
        var offsetY = (canvasHeight - displayedHeight) / 2.0;

        centerX = offsetX + match.NormalizedCenterX!.Value * displayedWidth;
        centerY = offsetY + match.NormalizedCenterY!.Value * displayedHeight;

        var normalizedRadius = match.NormalizedRadius ?? 0.06;
        radius = Math.Clamp(
            normalizedRadius * Math.Min(displayedWidth, displayedHeight),
            18,
            90);

        evidence = match;
        return true;
    }

    private void ImageSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        if (_findingFocusMode)
        {
            ResetFindingFocusMode();
            e.Handled = true;
            return;
        }

        if (!TryGetSelectedOverlayGeometry(
                out _,
                out var centerX,
                out var centerY,
                out var radius,
                out _,
                out _))
        {
            return;
        }

        var point = e.GetPosition(OverlayCanvas);
        var distance = Math.Sqrt(
            Math.Pow(point.X - centerX, 2) +
            Math.Pow(point.Y - centerY, 2));

        if (distance > Math.Max(36, radius * 1.25))
            return;

        EnterFindingFocusMode();
        e.Handled = true;
    }

    private void EnterFindingFocusMode()
    {
        if (!TryGetSelectedOverlayGeometry(
                out var evidence,
                out var centerX,
                out var centerY,
                out _,
                out var canvasWidth,
                out var canvasHeight))
        {
            StatusText.Text = "Focus mode unavailable for this finding/frame.";
            return;
        }

        StopPlayback();

        var scale = FindingFocusScale;
        var translateX = (canvasWidth / 2.0) - (centerX * scale);
        var translateY = (canvasHeight / 2.0) - (centerY * scale);

        ImageSurface.RenderTransformOrigin = new Point(0, 0);
        ImageSurface.RenderTransform = new MatrixTransform(
            new Matrix(scale, 0, 0, scale, translateX, translateY));

        _findingFocusMode = true;
        DrawSelectedFindingOverlay();

        var estimateText = string.Equals(
                _selectedFinding?.FindingType,
                "SuspectedTotalOcclusion",
                StringComparison.OrdinalIgnoreCase)
            ? "possible total occlusion"
            : _selectedFinding?.EstimatedDiameterStenosisPercent is double estimate
                ? $"QCA ~{estimate:0}% diameter stenosis"
                : $"candidate score {_selectedFinding?.Confidence:0.00}";

        StatusText.Text =
            $"Finding Focus Mode — {estimateText}. Research estimate only; press Esc to return to full cine.";

        _audit.Write("finding_focus_entered", new
        {
            findingId = _selectedFinding?.Id,
            evidence.SourceId,
            evidence.FrameStart,
            scale
        });
    }

    private void ResetFindingFocusMode(bool updateStatus = true)
    {
        if (!_findingFocusMode && ImageSurface.RenderTransform == Transform.Identity)
            return;

        ImageSurface.RenderTransform = Transform.Identity;
        _findingFocusMode = false;
        DrawSelectedFindingOverlay();

        if (updateStatus)
            StatusText.Text = "Finding Focus Mode closed — full cine restored.";

        _audit.Write("finding_focus_exited");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _findingFocusMode)
        {
            ResetFindingFocusMode();
            e.Handled = true;
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

        if (string.Equals(
                finding.FindingType,
                "SuspectedTotalOcclusion",
                StringComparison.OrdinalIgnoreCase))
        {
            var supportingFrames = finding.TotalOcclusionFrameCount ?? 0;
            _voice.Speak(
                $"Doctor, the separate research occlusion detector flagged a possible total occlusion{frameText}. " +
                $"It persisted across {supportingFrames} frames. Please review the evidence. " +
                "Chronicity is not established, so this is not by itself a C T O diagnosis.");
        }
        else if (finding.EstimatedDiameterStenosisPercent is double estimate &&
            finding.EstimatedDiameterStenosisLowerPercent is double lower &&
            finding.EstimatedDiameterStenosisUpperPercent is double upper)
        {
            var qualityText = string.IsNullOrWhiteSpace(finding.MeasurementQuality)
                ? string.Empty
                : $" Measurement quality is {finding.MeasurementQuality}.";
            var frameCountText = finding.MeasurementFrameCount is int frameCount
                ? $" The estimate was repeated across {frameCount} measured frames."
                : string.Empty;

            _voice.Speak(
                $"Doctor, research Q C A flagged a possible narrowed vessel region{frameText}. " +
                $"Estimated diameter stenosis is about {estimate:0} percent, " +
                $"with a research range from {lower:0} to {upper:0} percent." +
                qualityText + frameCountText +
                " Please review the evidence. This is not certified clinical Q C A or a diagnosis.");
        }
        else
        {
            _voice.Speak(
                $"Doctor, research-only AI flagged a stenosis candidate{frameText}. " +
                "Please review the evidence. This is not a diagnosis.");
        }
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

        ResetFindingFocusMode(false);
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
        ModelStatusText.Text = "Research AI model: NOT CONNECTED";
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
            var severityLabel = string.Equals(
                    finding.FindingType,
                    "SuspectedTotalOcclusion",
                    StringComparison.OrdinalIgnoreCase)
                ? "[possible total occlusion]"
                : finding.EstimatedDiameterStenosisPercent is double estimate
                    ? $"[~{estimate:0}% QCA]"
                    : $"[score {finding.Confidence:0.00}]";
            var viewLabel = finding.MultiViewConfirmed
                ? $"[{finding.SourceSeriesCount ?? 0} series/{finding.ProjectionCount ?? 0} views]"
                : string.Empty;

            var item = new ListBoxItem
            {
                Content =
                    $"{marker} {finding.Vessel} {finding.Segment} — {HumanizeFindingType(finding.FindingType)} " +
                    $"{severityLabel} " +
                    $"{(!string.IsNullOrWhiteSpace(finding.MeasurementQuality) ? $"[{finding.MeasurementQuality}]" : string.Empty)} " +
                    $"{viewLabel} [{sourceMarker}] [{finding.Status}]",
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

    private static string BuildResearchEstimateLabel(GuardianFinding finding)
    {
        if (string.Equals(
                finding.FindingType,
                "SuspectedTotalOcclusion",
                StringComparison.OrdinalIgnoreCase))
        {
            var builder = new StringBuilder();
            builder.Append("Separate total-occlusion detector: possible complete occlusion");
            if (finding.TotalOcclusionScore is double score)
                builder.Append($" | Detector score: {score:0.00}");
            if (finding.TotalOcclusionFrameCount is int frames)
                builder.Append($" | Supporting frames: {frames}");
            if (!string.IsNullOrWhiteSpace(finding.MeasurementQuality))
                builder.Append($" | Quality: {finding.MeasurementQuality}");
            builder.AppendLine();
            builder.Append("If the physician confirms total occlusion, anatomic diameter stenosis is 100%. ");
            builder.Append("Chronicity is not established by the image detector; this is not by itself a CTO diagnosis.");
            return builder.ToString();
        }

        if (finding.EstimatedDiameterStenosisPercent is not double estimate)
            return "Research QCA: measurement withheld because current evidence did not pass quality gates.";

        var builder = new StringBuilder();
        builder.Append($"Research QCA diameter stenosis: ~{estimate:0.#}%");

        if (finding.EstimatedDiameterStenosisLowerPercent is double lower &&
            finding.EstimatedDiameterStenosisUpperPercent is double upper)
        {
            builder.Append($" (range {lower:0.#}-{upper:0.#}%)");
        }

        if (!string.IsNullOrWhiteSpace(finding.MeasurementQuality))
            builder.Append($" | Quality: {finding.MeasurementQuality}");

        if (finding.MeasurementFrameCount is int frames)
            builder.Append($" | Measured frames: {frames}");

        if (finding.MeasurementVariabilityPercent is double variability)
            builder.Append($" | Variability: {variability:0.#} pp");

        if (finding.MultiViewConfirmed)
        {
            builder.Append(
                $" | Multi-view: {finding.SourceSeriesCount ?? 0} series / {finding.ProjectionCount ?? 0} projection(s)");
            if (finding.CrossViewVariabilityPercent is double crossView)
                builder.Append($" | Cross-view variability: {crossView:0.#} pp");
        }

        builder.AppendLine();

        if (finding.ReferenceDiameterMm is double referenceMm &&
            finding.MinimumLumenDiameterMm is double mldMm)
        {
            builder.Append($"Reference: {referenceMm:0.00} mm | MLD: {mldMm:0.00} mm");
            if (finding.LesionLengthMm is double lengthMm)
                builder.Append($" | Lesion length: {lengthMm:0.0} mm");

            if (!string.IsNullOrWhiteSpace(finding.CalibrationSource))
                builder.Append($" | {finding.CalibrationSource}");
        }
        else if (finding.ReferenceDiameterPixels is double referencePx &&
                 finding.MinimumLumenDiameterPixels is double mldPx)
        {
            builder.Append($"Reference: {referencePx:0.0} px | MLD: {mldPx:0.0} px");
            if (finding.LesionLengthPixels is double lengthPx)
                builder.Append($" | Lesion length: {lengthPx:0.0} px");
            builder.Append(" | Physical calibration withheld");
        }

        if (finding.FrameQualityScore is double frameQuality)
            builder.Append($" | Frame quality: {frameQuality:0.00}");
        if (finding.BorderConfidence is double border)
            builder.Append($" | Border confidence: {border:0.00}");

        builder.AppendLine();
        builder.Append("Research measurement — not certified clinical QCA.");

        return builder.ToString();
    }

    private static string HumanizeFindingType(string type) =>
        type.Replace("_", " ", StringComparison.Ordinal).Trim();
}

internal static class UiStringExtensions
{
    public static string OrFallback(this string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
