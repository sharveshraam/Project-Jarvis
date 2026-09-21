package dev.jarvis.core

import dev.jarvis.core.config.ConfigSuite
import dev.jarvis.core.crypto.CryptoSuite
import dev.jarvis.core.memory.Bm25Suite
import dev.jarvis.core.memory.EmbeddingSuite
import dev.jarvis.core.memory.MemorySuite
import dev.jarvis.core.memory.TopicKeySuite
import dev.jarvis.core.nlu.EntitiesSuite
import dev.jarvis.core.nlu.IntentRulesSuite
import dev.jarvis.core.nlu.NluEngineSuite
import dev.jarvis.core.nlu.TemporalSuite
import dev.jarvis.core.testing.TestSuite
import dev.jarvis.core.util.JsonSuite
import dev.jarvis.core.util.TextSuite

/**
 * The registry of every offline behavioural suite.
 *
 * Both runners consume this list:
 *  * `tools/local_core_check.sh` -> `LocalTestMain` (no test framework at all)
 *  * Gradle / CI               -> `CoreSelfTestJUnit` (thin JUnit bridge)
 *
 * Adding a suite means adding one line here, which guarantees the offline runner and CI can
 * never drift apart.
 */
object AllSuites {
    val all: List<TestSuite> = listOf(
        // foundations
        JsonSuite,
        TextSuite,
        CryptoSuite,
        ConfigSuite,
        // memory and retrieval
        EmbeddingSuite,
        Bm25Suite,
        TopicKeySuite,
        MemorySuite,
        // understanding
        TemporalSuite,
        IntentRulesSuite,
        EntitiesSuite,
        NluEngineSuite,
    )
}
