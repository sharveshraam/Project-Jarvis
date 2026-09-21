package dev.jarvis.app

import android.content.Context

/**
 * The application's object graph.
 *
 * Jarvis wires its dependencies by hand. The reasons are practical, not ideological:
 *
 *  * The graph has to be constructible from an [android.app.Service] and a
 *    [android.content.BroadcastReceiver], not just an `Activity` - the assistant spends
 *    most of its life outside the UI.
 *  * Annotation processors would add kapt/ksp to the build, which is the single most
 *    common cause of a broken Android build and would make the project harder to build
 *    offline.
 *  * Every subsystem in `:core` is already expressed as a port (interface), so swapping
 *    an implementation is a one-line change here.
 *
 * Stage 1 provides the graph shell; each later stage adds the adapters it needs.
 */
class JarvisGraph private constructor(
    val appContext: Context,
) {
    companion object {
        fun create(context: Context): JarvisGraph = JarvisGraph(context.applicationContext)
    }
}
