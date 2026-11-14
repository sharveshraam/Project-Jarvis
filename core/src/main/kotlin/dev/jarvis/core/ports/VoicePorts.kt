package dev.jarvis.core.ports

/**
 * Voice ports: recognition, synthesis and wake-word detection.
 *
 * All three are replaceable, and all three carry an honesty flag about whether they actually
 * ran on the device. That flag matters: on many phones the only SpeechRecognizer installed is
 * Google's, which sends audio to the cloud. The assistant must be able to tell the user that
 * what they just said left the phone - it cannot infer that from behaviour.
 */

data class SpeechRecognitionResult(
    val text: String,
    val confidence: Float,
    val isFinal: Boolean,
    /**
     * Null when the recogniser does not report it, which most do not. The app layer combines
     * this with its own knowledge of which recogniser is installed to give the user a best
     * answer rather than no answer.
     */
    val onDevice: Boolean?,
)

interface SpeechToTextPort {
    val isAvailable: Boolean

    /** Human-readable description of the recogniser in use, for the Voice settings screen. */
    val recognizerDescription: String

    val isListening: Boolean

    fun startListening(
        language: String,
        onPartial: (String) -> Unit,
        onResult: (SpeechRecognitionResult) -> Unit,
        onError: (String) -> Unit,
    )

    fun stopListening()

    /** Abandons recognition without producing a result. Used when the user interrupts. */
    fun cancel()
}

data class TtsVoice(
    val id: String,
    val name: String,
    val locale: String,
    /** True when the engine would need the network to use this voice. */
    val requiresNetwork: Boolean?,
)

interface TextToSpeechPort {
    val isAvailable: Boolean

    /** Description of the installed engine, so the user knows whose voice this is. */
    val engineDescription: String

    fun voices(locale: String?): List<TtsVoice>

    fun setVoice(id: String): PortResult

    fun setPitch(pitch: Float): PortResult

    fun setRate(rate: Float): PortResult

    /**
     * Speaks [text].
     *
     * [interrupt] is how "stop talking" and barge-in work: the user can cut the assistant off
     * mid-sentence, which requirement §8 asks for explicitly.
     */
    fun speak(text: String, interrupt: Boolean, onDone: (() -> Unit)? = null): PortResult

    fun stop(): PortResult

    val isSpeaking: Boolean
}

/**
 * Wake-word detection.
 *
 * The honest constraint, stated here so it cannot be lost: Android does not let a third-party
 * app register a system-level hotword. That is reserved for the OEM assistant. So a wake word
 * here means a foreground service holding the microphone open, with a visible ongoing
 * notification and a real battery cost - and detection is a lightweight on-device matcher, not
 * a trained model. [detectorDescription] is surfaced in Settings so none of that is hidden.
 */
interface WakeWordPort {
    val isRunning: Boolean

    /** True when detection is entirely on-device. False means it is not offered at all. */
    val isOnDevice: Boolean

    /** Shown verbatim in Settings > Voice, e.g. "on-device template matcher, no neural model". */
    val detectorDescription: String

    fun start(
        wakeWord: String,
        onDetected: (String) -> Unit,
        onError: (String) -> Unit,
    ): PortResult

    fun stop(): PortResult

    /** Re-enrols the matcher for a new nickname; the wake word follows the assistant's name. */
    fun setWakeWord(wakeWord: String): PortResult
}
