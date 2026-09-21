package dev.jarvis.core.model

/**
 * Teaching model.
 *
 * Teaching mode is not "answer the question". It is a staged loop that assesses the learner,
 * explains, gives an example, asks the user to attempt a problem, evaluates the attempt,
 * explains the mistake and adjusts difficulty - and it remembers all of that locally.
 */

enum class DifficultyLevel(val rank: Int, val label: String) {
    BEGINNER(0, "Beginner"),
    INTERMEDIATE(1, "Intermediate"),
    ADVANCED(2, "Advanced"),
    ;

    fun harder(): DifficultyLevel = entries.firstOrNull { it.rank == rank + 1 } ?: this
    fun easier(): DifficultyLevel = entries.firstOrNull { it.rank == rank - 1 } ?: this
}

/** How the user prefers to be taught. Configurable, and adapted per topic from results. */
enum class LearningStyle(val label: String) {
    EXAMPLE_FIRST("Show me an example first"),
    THEORY_FIRST("Explain the theory first"),
    SOCRATIC("Ask me questions and let me reason it out"),
    VISUAL("Describe diagrams and layouts"),
    MIXED("Mix explanation, examples and practice"),
}

/**
 * Where a lesson currently is. The engine advances through these; the UI shows them so the
 * user always knows what is coming next.
 */
enum class TeachingStage(val label: String) {
    ASSESS_LEVEL("Checking your starting level"),
    EXPLAIN("Explaining the concept"),
    EXAMPLE("Working through an example"),
    PRACTICE("Your turn to try"),
    EVALUATE("Checking your answer"),
    REMEDIATE("Working through what went wrong"),
    ADVANCE("Moving to a harder problem"),
    CONSOLIDATE("Summarising what you have learned"),
    COMPLETE("Topic complete"),
    BLOCKED("Cannot teach this right now"),
}

enum class TopicStatus { NOT_STARTED, LEARNING, PRACTICING, CONSOLIDATING, MASTERED, ABANDONED }

/** What kind of answer the engine expects, so the UI can show the right input. */
enum class InputKind { NONE, FREE_TEXT, NUMERIC, MULTIPLE_CHOICE }

/**
 * Per-topic learning state. Persisted locally; this is what makes "continue where we left
 * off" and "adjust the difficulty" possible across sessions.
 */
data class TopicProgress(
    val topic: String,
    val topicKey: String,
    val status: TopicStatus = TopicStatus.NOT_STARTED,
    val level: DifficultyLevel = DifficultyLevel.BEGINNER,
    val attempts: Int = 0,
    val correct: Int = 0,
    val incorrect: Int = 0,
    /** 0..1, an exponentially smoothed accuracy that drives difficulty adaptation. */
    val mastery: Double = 0.0,
    /** Normalised descriptions of recurring mistakes, used to target remediation. */
    val recurringMistakes: List<String> = emptyList(),
    val firstStudiedAt: Long? = null,
    val lastStudiedAt: Long? = null,
    val totalMillis: Long = 0L,
    /** Material the user supplied for this topic, from the knowledge base. */
    val sourceDocumentIds: List<String> = emptyList(),
) {
    val accuracy: Double
        get() = if (attempts == 0) 0.0 else correct.toDouble() / attempts

    val isMastered: Boolean get() = status == TopicStatus.MASTERED
}

/** Aggregated learning profile - the local record of what the user has studied. */
data class LearningProfile(
    val preferredStyle: LearningStyle = LearningStyle.MIXED,
    val topics: Map<String, TopicProgress> = emptyMap(),
    val updatedAt: Long = 0L,
) {
    fun progress(topicKey: String): TopicProgress? = topics[topicKey]

    val activeTopics: List<TopicProgress>
        get() = topics.values.filter { it.status == TopicStatus.LEARNING || it.status == TopicStatus.PRACTICING }

    val masteredTopics: List<TopicProgress>
        get() = topics.values.filter { it.status == TopicStatus.MASTERED }
}

/** One turn of a lesson, produced by the engine and rendered by the UI. */
data class LessonTurn(
    val id: String,
    val sessionId: String,
    val topic: String,
    val stage: TeachingStage,
    val level: DifficultyLevel,
    /** What the assistant says. */
    val prompt: String,
    val expectedInput: InputKind = InputKind.NONE,
    val choices: List<String> = emptyList(),
    val hints: List<String> = emptyList(),
    /** Non-null when the engine has a definite answer it will check against. */
    val expectedAnswer: String? = null,
    val tolerance: Double? = null,
    val createdAt: Long = 0L,
    /** Where the teaching content came from - always disclosed. */
    val contentSource: ContentSource = ContentSource.LOCAL_TEMPLATE,
    val documentIds: List<String> = emptyList(),
)

/** Provenance of teaching content. The user must be able to tell these apart. */
enum class ContentSource(val label: String) {
    /** Generated locally from the engine's templates and the user's own material. */
    LOCAL_TEMPLATE("Generated on your device"),

    /** Retrieved from a document the user ingested. */
    LOCAL_KNOWLEDGE("From your notes"),

    /** Cached locally after a user-approved online fetch. */
    CACHED_ONLINE("Cached from an online source"),

    /** Fetched online for this request, with the user's explicit opt-in. */
    ONLINE("Fetched online just now"),
}

/** Result of evaluating a learner's attempt. */
data class AnswerEvaluation(
    val correct: Boolean,
    /** 0..1, partial credit is real: a right method with an arithmetic slip is not zero. */
    val score: Double,
    val feedback: String,
    val mistakes: List<String> = emptyList(),
    val nextLevel: DifficultyLevel,
    val shouldRemediate: Boolean,
)

/**
 * A teaching session in progress. Held in memory by the engine and persisted at stage
 * boundaries so an interrupted lesson can resume.
 */
data class TeachingSession(
    val id: String,
    val topic: String,
    val topicKey: String,
    val level: DifficultyLevel,
    val stage: TeachingStage,
    val startedAt: Long,
    val updatedAt: Long,
    val turns: List<LessonTurn> = emptyList(),
    val pendingQuestion: LessonTurn? = null,
    val contentSource: ContentSource = ContentSource.LOCAL_TEMPLATE,
    val documentIds: List<String> = emptyList(),
    val finished: Boolean = false,
)
