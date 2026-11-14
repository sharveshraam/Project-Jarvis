using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Windows;

/// <summary>Push-to-talk speech-to-text over Windows Speech (System.Speech).
/// The microphone is only opened inside <see cref="ListenOnceAsync"/> — i.e.
/// after an explicit push-to-talk action. Nothing listens in the background.</summary>
public interface ISpeechToText
{
    Task<string?> ListenOnceAsync(string culture, CancellationToken ct = default);
}

public sealed class WindowsSpeechToText(ILogger<WindowsSpeechToText>? logger = null) : ISpeechToText
{
    private readonly ILogger<WindowsSpeechToText> _log = logger ?? NullLogger<WindowsSpeechToText>.Instance;

    public async Task<string?> ListenOnceAsync(string culture, CancellationToken ct = default)
    {
        try
        {
            var cultureInfo = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(culture) ? "en-GB" : culture);
            using var recognizer = new SpeechRecognitionEngine(cultureInfo);
            recognizer.SetInputToDefaultAudioDevice();
            recognizer.LoadGrammar(new DictationGrammar());
            var tcs = new TaskCompletionSource<string?>();
            recognizer.SpeechRecognized += (_, e) => tcs.TrySetResult(e.Result.Text);
            recognizer.SpeechRecognitionRejected += (_, _) => tcs.TrySetResult(null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            recognizer.RecognizeAsync();
            var result = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            recognizer.RecognizeAsyncCancel();
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Speech recognition unavailable");
            return null;
        }
    }
}

/// <summary>Text-to-speech over Windows Speech. Supports interruption via
/// <see cref="Stop"/> (the UI's interrupt button).</summary>
public interface ITextToSpeech
{
    Task SpeakAsync(string text, string culture, CancellationToken ct = default);
    void Stop();
}

public sealed class WindowsTextToSpeech(ILogger<WindowsTextToSpeech>? logger = null) : ITextToSpeech
{
    private readonly ILogger<WindowsTextToSpeech> _log = logger ?? NullLogger<WindowsTextToSpeech>.Instance;
    private SpeechSynthesizer? _synth;

    public async Task SpeakAsync(string text, string culture, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _synth?.Dispose();
            _synth = new SpeechSynthesizer();
            _synth.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, culture);
            _synth.Rate = 0;
            await _synth.SpeakAsync(text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Speech synthesis unavailable");
        }
    }

    public void Stop()
    {
        try { _synth?.SpeakAsyncCancelAll(); } catch { /* already stopped */ }
    }
}
