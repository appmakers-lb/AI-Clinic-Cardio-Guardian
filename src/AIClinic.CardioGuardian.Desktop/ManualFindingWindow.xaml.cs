using System.Windows;
using System.Windows.Controls;
using AIClinic.CardioGuardian.Core.Models;

namespace AIClinic.CardioGuardian.Desktop;

public partial class ManualFindingWindow : Window
{
    private readonly string _sourceId;
    private readonly int _frame;
    private readonly string? _projection;

    public GuardianFinding? Result { get; private set; }

    public ManualFindingWindow(string sourceId, int frame, string? projection)
    {
        InitializeComponent();
        _sourceId = sourceId;
        _frame = frame;
        _projection = projection;
        SourceBox.Text = sourceId;
        FrameBox.Text = frame.ToString();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var vessel = GetSelected(VesselBox);
        var segment = GetSelected(SegmentBox);
        var findingType = GetSelected(FindingTypeBox);
        var priorityText = GetSelected(PriorityBox);

        if (!double.TryParse(ConfidenceBox.Text, out var percent) || percent is < 0 or > 100)
        {
            MessageBox.Show("Confidence must be between 0 and 100.", "Invalid confidence",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Enum.TryParse<FindingPriority>(priorityText, out var priority))
            priority = FindingPriority.Review;

        Result = new GuardianFinding
        {
            Id = $"MAN-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
            Vessel = vessel,
            Segment = segment,
            FindingType = findingType,
            Confidence = percent / 100.0,
            Priority = priority,
            Source = FindingSource.ManualResearch,
            SourceVersion = "physician-manual-research",
            Explanation = ExplanationBox.Text.Trim(),
            Evidence = new[]
            {
                new EvidenceReference(_sourceId, _frame, _frame, _projection, "Manual research annotation at current frame")
            }
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string GetSelected(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
}
