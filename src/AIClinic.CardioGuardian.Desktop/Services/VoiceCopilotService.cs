using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace AIClinic.CardioGuardian.Desktop.Services;

public sealed class VoiceCopilotService : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private SpeechRecognitionEngine? _recognizer;

    public bool SpeechSynthesisAvailable { get; private set; } = true;
    public bool SpeechRecognitionAvailable => _recognizer is not null;
    public string RecognitionStatus { get; private set; } = "Not initialized";

    public VoiceCopilotService()
    {
        try
        {
            _synthesizer.SetOutputToDefaultAudioDevice();
        }
        catch
        {
            SpeechSynthesisAvailable = false;
        }

        TryInitializeRecognition();
    }

    public void Speak(string text)
    {
        if (!SpeechSynthesisAvailable || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SpeakAsync(text);
        }
        catch
        {
            SpeechSynthesisAvailable = false;
        }
    }

    public async Task<string?> ListenForCommandAsync(CancellationToken cancellationToken = default)
    {
        if (_recognizer is null) return null;

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = _recognizer.Recognize(TimeSpan.FromSeconds(5));
                return result?.Text;
            }
            catch
            {
                return null;
            }
        }, cancellationToken);
    }

    private void TryInitializeRecognition()
    {
        try
        {
            var info = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault();
            if (info is null)
            {
                RecognitionStatus = "No Windows speech recognizer installed";
                return;
            }

            _recognizer = new SpeechRecognitionEngine(info);
            var commands = new Choices(
                "show evidence",
                "show me why",
                "coverage",
                "what is incomplete",
                "anything else",
                "show findings",
                "mute voice",
                "unmute voice",
                "next cine",
                "next series",
                "previous cine",
                "previous series",
                "play",
                "play cine",
                "pause",
                "pause cine",
                "stop",
                "stop cine");

            var builder = new GrammarBuilder(commands) { Culture = info.Culture };
            _recognizer.LoadGrammar(new Grammar(builder));
            _recognizer.SetInputToDefaultAudioDevice();
            RecognitionStatus = $"Ready ({info.Culture.Name})";
        }
        catch (Exception ex)
        {
            _recognizer?.Dispose();
            _recognizer = null;
            RecognitionStatus = $"Unavailable: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _synthesizer.Dispose();
    }
}
