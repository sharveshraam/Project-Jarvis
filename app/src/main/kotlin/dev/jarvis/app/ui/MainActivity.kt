package dev.jarvis.app.ui

import android.os.Bundle
import androidx.appcompat.app.AppCompatActivity
import dev.jarvis.app.databinding.ActivityMainBinding

/**
 * The assistant's primary surface.
 *
 * Stage 1 of the roadmap: this is the application shell. It establishes the view-binding
 * pattern, the themed transcript surface and the provenance banner that every response
 * must respect ("local" vs "online"). The conversation itself is wired to the core engine
 * in Stage 4.
 */
class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)
    }
}
