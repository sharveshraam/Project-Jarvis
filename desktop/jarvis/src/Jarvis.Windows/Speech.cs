using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Text;
using Microsoft.Extensions.Logging;

namespace Jarvis.Windows;

/// <summary>
/// Push-to-talk dictation using the speech recogniser built into Windows. The microphone is opened by
/// <see cref="Start"/> and released by <see cref="StopAsync"/>; nothing listens in between, and audio never leaves the PC.
/// </summary>
public sealed class WindowsSpeechToText : ISpeechToText, IDisposable
{
    private readonly ILogger<WindowsSpeechToText> _log;
    private readonly object _gate = new object();
    private readonly StringBuilder _text = new StringBuilder();
    private SpeechRecognitionEngine? _engine;
    private TaskCompletionSource<string>? _stopped;
    private volatile bool _listening;

    public WindowsSpeechToText(ILogger<WindowsSpeechToText> log) => _log = log;

    public bool IsListening => _listening;

    public event Action<string>? PartialResult;

    public bool IsAvailable(out string reason)
    {
        try
        {
            if (SpeechRecognitionEngine.InstalledRecognizers().Count == 0)
            {
                reason = "No Windows speech recognition language is installed. Add one under Windows Settings › Time & language › Speech.";
                return false;
            }
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = "Windows speech recognition is not available: " + ex.Message;
            return false;
        }
    }

    private SpeechRecognitionEngine EnsureEngine()
    {
        if (_engine is not null) return _engine;
        var engine = new SpeechRecognitionEngine();
        engine.LoadGrammar(new DictationGrammar());
        engine.SpeechHypothesized += (sender, e) =>
        {
            string preview;
            lock (_gate) preview = (_text.ToString() + " " + e.Result.Text).Trim();
            PartialResult?.Invoke(preview);
        };
        engine.SpeechRecognized += (sender, e) =>
        {
            if (e.Result is null || string.IsNullOrWhiteSpace(e.Result.Text)) return;
            string preview;
            lock (_gate)
            {
                if (_text.Length > 0) _text.Append(' ');
                _text.Append(e.Result.Text);
                preview = _text.ToString();
            }
            PartialResult?.Invoke(preview);
        };
        engine.RecognizeCompleted += (sender, e) => Finish();
        _engine = engine;
        return engine;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_listening) return;
            var engine = EnsureEngine();
            _text.Clear();
            _stopped = null;
            try
            {
                engine.SetInputToDefaultAudioDevice();
                engine.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch (Exception ex)
            {
                try { engine.SetInputToNull(); }
                catch (Exception) { }
                throw new InvalidOperationException("The microphone could not be opened. Check that one is connected and allowed under Windows Settings › Privacy › Microphone.", ex);
            }
            _listening = true;
        }
    }

    public async Task<string> StopAsync()
    {
        TaskCompletionSource<string> completion;
        SpeechRecognitionEngine? engine;
        lock (_gate)
        {
            // Recognition may already have ended on its own; whatever was heard is still in the buffer.
            if (!_listening || _engine is null) return _text.ToString().Trim();
            engine = _engine;
            _stopped ??= new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _stopped;
        }

        try
        {
            // Lets the phrase in progress finish, then raises RecognizeCompleted.
            engine.RecognizeAsyncStop();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Speech recognition did not stop cleanly.");
            Finish();
        }

        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false);
        if (finished != completion.Task)
        {
            try { engine.RecognizeAsyncCancel(); }
            catch (Exception) { }
            Finish();
        }
        return await completion.Task.ConfigureAwait(false);
    }

    private void Finish()
    {
        string text;
        TaskCompletionSource<string>? completion;
        lock (_gate)
        {
            if (!_listening) return;
            _listening = false;
            text = _text.ToString().Trim();
            completion = _stopped;
            _stopped = null;
            try { _engine?.SetInputToNull(); }
            catch (Exception) { }
        }
        completion?.TrySetResult(text);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _engine?.RecognizeAsyncCancel(); }
            catch (Exception) { }
            _engine?.Dispose();
            _engine = null;
            _listening = false;
        }
    }
}

/// <summary>Spoken replies with the installed Windows voices. Speech can be interrupted at any moment.</summary>
public sealed class WindowsTextToSpeech : ITextToSpeech, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ILogger<WindowsTextToSpeech> _log;
    private readonly object _gate = new object();
    private SpeechSynthesizer? _synthesizer;
    private volatile bool _speaking;

    public WindowsTextToSpeech(ISettingsStore settings, ILogger<WindowsTextToSpeech> log)
    {
        _settings = settings;
        _log = log;
    }

    public bool IsSpeaking => _speaking;

    public static IReadOnlyList<string> InstalledVoices()
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            return synthesizer.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private SpeechSynthesizer Ensure()
    {
        lock (_gate)
        {
            if (_synthesizer is null)
            {
                _synthesizer = new SpeechSynthesizer();
                _synthesizer.SetOutputToDefaultAudioDevice();
            }
            return _synthesizer;
        }
    }

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        var spoken = SpeechText.ForSpeaking(text);
        if (spoken.Length == 0) return;

        SpeechSynthesizer synthesizer;
        try
        {
            synthesizer = Ensure();
            synthesizer.SpeakAsyncCancelAll();
            var settings = _settings.Current;
            synthesizer.Rate = Math.Clamp(settings.SpeechRate, -10, 10);
            if (!string.IsNullOrWhiteSpace(settings.VoiceName))
            {
                try { synthesizer.SelectVoice(settings.VoiceName); }
                catch (ArgumentException) { /* voice was uninstalled: keep the default */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Text-to-speech is unavailable.");
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Prompt? prompt = null;
        EventHandler<SpeakCompletedEventArgs> handler = (sender, e) =>
        {
            if (prompt is null || ReferenceEquals(e.Prompt, prompt)) completion.TrySetResult(true);
        };
        synthesizer.SpeakCompleted += handler;
        try
        {
            _speaking = true;
            prompt = synthesizer.SpeakAsync(spoken);
            using (ct.Register(() => { try { synthesizer.SpeakAsyncCancelAll(); } catch (Exception) { } }))
            {
                await completion.Task.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Speaking failed.");
        }
        finally
        {
            synthesizer.SpeakCompleted -= handler;
            _speaking = false;
        }
    }

    public void Stop()
    {
        try
        {
            lock (_gate) _synthesizer?.SpeakAsyncCancelAll();
        }
        catch (Exception)
        {
            // Nothing was speaking.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _synthesizer?.SpeakAsyncCancelAll(); }
            catch (Exception) { }
            _synthesizer?.Dispose();
            _synthesizer = null;
        }
    }
}
