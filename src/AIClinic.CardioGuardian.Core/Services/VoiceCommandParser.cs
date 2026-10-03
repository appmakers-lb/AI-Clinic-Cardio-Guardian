namespace AIClinic.CardioGuardian.Core.Services;

public enum VoiceIntent
{
    Unknown,
    NextCine,
    PreviousCine,
    ShowEvidence,
    CoverageSummary,
    FindingsSummary,
    MuteVoice,
    UnmuteVoice,
    Play,
    Pause,
    Stop
}

public sealed record VoiceCommand(VoiceIntent Intent, string NormalizedText);

public sealed class VoiceCommandParser
{
    public VoiceCommand Parse(string? text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
            return new(VoiceIntent.Unknown, normalized);

        return normalized switch
        {
            "next cine" or "next series" or "next run" =>
                new(VoiceIntent.NextCine, normalized),

            "previous cine" or "previous series" or "previous run" =>
                new(VoiceIntent.PreviousCine, normalized),

            "show evidence" or "show me why" or "why" =>
                new(VoiceIntent.ShowEvidence, normalized),

            "coverage" or "coverage summary" or "what is incomplete" or
            "what's incomplete" or "what is not assessed" =>
                new(VoiceIntent.CoverageSummary, normalized),

            "show findings" or "findings" or "anything else" =>
                new(VoiceIntent.FindingsSummary, normalized),

            "mute voice" =>
                new(VoiceIntent.MuteVoice, normalized),

            "unmute voice" =>
                new(VoiceIntent.UnmuteVoice, normalized),

            "play" or "play cine" =>
                new(VoiceIntent.Play, normalized),

            "pause" or "pause cine" =>
                new(VoiceIntent.Pause, normalized),

            "stop" or "stop cine" =>
                new(VoiceIntent.Stop, normalized),

            _ => new(VoiceIntent.Unknown, normalized)
        };
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ',
                value.Trim()
                    .ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
