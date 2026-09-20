package dev.jarvis.app.ui

import android.app.Activity
import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import dev.jarvis.app.R

/**
 * The assistant's primary surface.
 *
 * Stage 1 of the roadmap: this is the application shell. It establishes the screen
 * structure, the transcript surface and the provenance banner that every response must
 * respect ("local" vs "online"). Conversation, voice and tools are wired in from
 * Stage 3 onwards.
 *
 * Views are resolved with findViewById rather than view binding on purpose - see the
 * comment in app/build.gradle.kts.
 */
class MainActivity : Activity() {

    private lateinit var titleText: TextView
    private lateinit var statusText: TextView
    private lateinit var transcriptText: TextView
    private lateinit var inputText: EditText

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        titleText = findViewById(R.id.titleText)
        statusText = findViewById(R.id.statusText)
        transcriptText = findViewById(R.id.transcriptText)
        inputText = findViewById(R.id.inputText)

        val sendButton = findViewById<Button>(R.id.sendButton)
        val micButton = findViewById<Button>(R.id.micButton)

        sendButton.setOnClickListener { submit(inputText.text.toString()) }
        // Voice arrives in Stage 5; until then the control is honest about being inert
        // rather than pretending to listen.
        micButton.isEnabled = false
    }

    private fun submit(rawInput: String) {
        val input = rawInput.trim()
        if (input.isEmpty()) return
        inputText.setText("")
        transcriptText.text = input
    }
}
