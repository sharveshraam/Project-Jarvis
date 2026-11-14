package dev.jarvis.core.capability

/**
 * The catalogue of everything the assistant can attempt, with an honest status for each.
 *
 * This is the single source of truth for "can Jarvis do X on *this* device right now?". The
 * engine consults it before promising anything, the UI renders it in
 * Settings > Capabilities, and a tool that cannot run reports its status instead of silently
 * failing or pretending to succeed.
 *
 * Statuses are resolved from a [DeviceCapabilityProbe], never assumed. Several capabilities
 * can never reach SUPPORTED on any Android device; those carry an [Capability.androidLimitation]
 * explaining why, and the assistant says so rather than implying a workaround exists.
 */
object CapabilityCatalog {

    // ---- ids ------------------------------------------------------------------------
    // Referenced by tools, the engine and the UI, so they are constants rather than strings
    // scattered through the codebase.
    const val LOCAL_UNDERSTANDING = "conversation.local_understanding"
    const val OPEN_ENDED_CONVERSATION = "conversation.open_ended"
    const val LOCAL_MODEL = "conversation.local_model"
    const val ONLINE_AI = "conversation.online_ai"

    const val LONG_TERM_MEMORY = "memory.long_term"
    const val MEMORY_RECALL = "memory.recall"
    const val MEMORY_FORGET = "memory.forget"
    const val SEMANTIC_MEMORY_SEARCH = "memory.semantic_search"
    const val MEMORY_EXPORT = "memory.export"

    const val READ_SCREEN = "screen.read"
    const val TAP_ELEMENT = "screen.tap"
    const val TYPE_TEXT = "screen.type_text"
    const val SCROLL = "screen.scroll"
    const val NAVIGATE_BACK = "screen.back"
    const val LIST_ITEM_SELECTION = "screen.list_item_selection"
    const val SCREEN_OCR = "screen.ocr"
    const val READ_SCREEN_WHILE_LOCKED = "screen.read_while_locked"
    const val MULTI_STEP_AGENT = "screen.multi_step_agent"

    const val VOICE_INPUT = "voice.input"
    const val VOICE_OUTPUT = "voice.output"
    const val WAKE_WORD = "voice.wake_word"
    const val VOICE_HISTORY = "voice.history"
    const val VOICE_WHILE_LOCKED = "voice.while_locked"

    const val OPEN_APP = "phone.open_app"
    const val MEDIA_CONTROL = "phone.media_control"
    const val VOLUME_CONTROL = "phone.volume"
    const val SEND_MESSAGE = "phone.send_message"
    const val READ_MESSAGES = "phone.read_messages"
    const val READ_MESSAGE_NOTIFICATIONS = "phone.read_message_notifications"
    const val PLACE_CALL = "phone.place_call"
    const val READ_CONTACTS = "phone.read_contacts"
    const val READ_NOTIFICATIONS = "phone.read_notifications"
    const val DEVICE_STATUS = "phone.device_status"

    const val REMINDERS = "schedule.reminders"
    const val ALARMS = "schedule.alarms"
    const val TIMERS = "schedule.timers"
    const val TASKS_AND_NOTES = "schedule.tasks_notes"
    const val AUTOMATION_TIME = "automation.time"
    const val AUTOMATION_BATTERY = "automation.battery"
    const val AUTOMATION_HEADSET = "automation.headset"
    const val AUTOMATION_CHARGER = "automation.charger"
    const val AUTOMATION_APP = "automation.app"
    const val AUTOMATION_LOCATION = "automation.location"
    const val AUTOMATION_NOTIFICATION = "automation.notification"
    const val AUTOMATION_SURVIVES_REBOOT = "automation.survives_reboot"

    const val TEACH_LOCAL = "teaching.local"
    const val TEACH_ONLINE = "teaching.online"
    const val LEARNING_PROGRESS = "teaching.progress"
    const val INGEST_PDF_TEXT = "knowledge.ingest_pdf_text"
    const val INGEST_SCANNED_PDF = "knowledge.ingest_scanned_pdf"
    const val INGEST_WEB_PAGE = "knowledge.ingest_web_page"
    const val KNOWLEDGE_SEARCH = "knowledge.search"

    const val WEB_SEARCH = "online.web_search"

    const val SECRET_PHRASE = "security.secret_phrase"
    const val BIOMETRIC_AUTH = "security.biometric"
    const val DEVICE_CREDENTIAL_AUTH = "security.device_credential"
    const val UNLOCK_SECURE_LOCK_SCREEN = "security.unlock_lock_screen"
    const val DATA_EXPORT = "security.data_export"
    const val DATA_DELETION = "security.data_deletion"
    const val NO_SILENT_UPLOAD = "security.no_silent_upload"

    private fun capability(
        id: String,
        name: String,
        group: CapabilityGroup,
        bestCase: CapabilityStatus,
        explanation: String,
        remedy: String? = null,
        androidLimitation: String? = null,
        resolve: (DeviceCapabilityProbe) -> CapabilityStatus,
    ) = Capability(
        id = id,
        name = name,
        group = group,
        bestCase = bestCase,
        explanation = explanation,
        remedy = remedy,
        androidLimitation = androidLimitation,
        resolve = resolve,
    )

    private const val ACCESSIBILITY_REMEDY =
        "Enable it under Settings > Accessibility > Jarvis screen agent."
    private const val NOTIFICATION_ACCESS_REMEDY =
        "Enable it under Settings > Notification access > Jarvis."

    val all: List<Capability> = listOf(
        // =============================================================== conversation ==
        capability(
            id = LOCAL_UNDERSTANDING,
            name = "Understanding commands on-device",
            group = CapabilityGroup.CONVERSATION,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Intent parsing, memory lookup, teaching, automation and tool use all " +
                "run entirely on the device. This never needs the internet.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = OPEN_ENDED_CONVERSATION,
            name = "Open-ended conversation",
            group = CapabilityGroup.CONVERSATION,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Free-form questions that are not commands need a language model. " +
                "Jarvis ships without one: it understands what it was built to understand, and " +
                "says so when you ask something outside that.",
            remedy = "Install an on-device model, or enable an online provider in Settings.",
        ) { probe ->
            when {
                probe.installedLocalModel() != null -> CapabilityStatus.SUPPORTED
                probe.isOnline() -> CapabilityStatus.ONLINE_REQUIRED
                else -> CapabilityStatus.MODEL_REQUIRED
            }
        },

        capability(
            id = LOCAL_MODEL,
            name = "On-device neural model",
            group = CapabilityGroup.CONVERSATION,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "A local model slot exists and is replaceable, but no model is bundled: " +
                "a useful one is hundreds of megabytes and is device-dependent.",
            remedy = "Install a model in Settings > AI providers.",
        ) { probe ->
            if (probe.installedLocalModel() != null) CapabilityStatus.SUPPORTED
            else CapabilityStatus.MODEL_REQUIRED
        },

        capability(
            id = ONLINE_AI,
            name = "Online AI provider",
            group = CapabilityGroup.ONLINE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Off by default. When on, every request that leaves the device is " +
                "labelled as online processing, and conversation history and memories are never " +
                "sent unless that specific request needs them.",
            remedy = "Enable it in Settings > AI providers, then connect to the internet.",
        ) { probe ->
            // Online AI is gated by configuration, which the probe does not know about; the
            // engine layers the DISABLED_BY_USER status on top via ConfiguredCapabilities.
            if (probe.isOnline()) CapabilityStatus.SUPPORTED else CapabilityStatus.ONLINE_REQUIRED
        },

        // ===================================================================== memory ==
        capability(
            id = LONG_TERM_MEMORY,
            name = "Long-term memory",
            group = CapabilityGroup.CONVERSATION,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Facts, preferences, projects and goals are stored locally in a " +
                "structured store with categories, topics and provenance - not as raw chat logs.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = MEMORY_RECALL,
            name = "Recalling what you told it",
            group = CapabilityGroup.CONVERSATION,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "\"What do you remember about X?\" searches the local memory store.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = MEMORY_FORGET,
            name = "Forgetting on request",
            group = CapabilityGroup.SECURITY,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Individual memories, whole topics, whole conversations and everything " +
                "at once can all be deleted, and deletion is immediate and local.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = SEMANTIC_MEMORY_SEARCH,
            name = "Paraphrase-tolerant memory search",
            group = CapabilityGroup.CONVERSATION,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "Search is a hybrid of stemmed lexical matching and hashing-based " +
                "embeddings. It handles word order, plurals and typos, but it is not a neural " +
                "embedding model, so a heavily reworded query can miss.",
            remedy = "Install an on-device model that provides real embeddings.",
        ) { CapabilityStatus.PARTIALLY_SUPPORTED },

        capability(
            id = MEMORY_EXPORT,
            name = "Exporting your data",
            group = CapabilityGroup.SECURITY,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Produces a JSON file you choose the destination for. Nothing is " +
                "uploaded by the app.",
        ) { CapabilityStatus.SUPPORTED },

        // ===================================================================== screen ==
        capability(
            id = READ_SCREEN,
            name = "Reading the current screen",
            group = CapabilityGroup.SCREEN,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Uses the Android accessibility node tree, so it reads real UI " +
                "semantics (buttons, lists, text fields, content descriptions) rather than " +
                "guessing from pixels.",
            remedy = ACCESSIBILITY_REMEDY,
        ) { probe ->
            if (probe.accessibilityServiceEnabled()) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = TAP_ELEMENT,
            name = "Tapping things on screen",
            group = CapabilityGroup.SCREEN,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Taps a node it identified semantically, and reports whether the " +
                "platform accepted the action.",
            remedy = ACCESSIBILITY_REMEDY,
        ) { probe ->
            if (probe.accessibilityServiceEnabled()) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = TYPE_TEXT,
            name = "Typing into fields",
            group = CapabilityGroup.SCREEN,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Sets text on the focused or identified editable field. Some apps use " +
                "custom text surfaces that refuse accessibility edits; when that happens Jarvis " +
                "says so instead of claiming it typed.",
            remedy = ACCESSIBILITY_REMEDY,
        ) { probe ->
            if (probe.accessibilityServiceEnabled()) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = SCROLL,
            name = "Scrolling",
            group = CapabilityGroup.SCREEN,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Scrolls a scrollable container forwards or backwards.",
            remedy = ACCESSIBILITY_REMEDY,
        ) { probe ->
            if (probe.accessibilityServiceEnabled()) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = NAVIGATE_BACK,
            name = "Back, home and recents",
            group = CapabilityGroup.SCREEN,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Global navigation actions, available to any enabled accessibility " +
                "service.",
            remedy = ACCESSIBILITY_REMEDY,
        ) { probe ->
            if (probe.accessibilityServiceEnabled()) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = LIST_ITEM_SELECTION,
            name = "\"The second recommended video\"",
            group = CapabilityGroup.SCREEN,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "Lists are detected structurally - a container whose children repeat " +
                "the same shape - and items are chosen by position and label, never by fixed " +
                "coordinates. But an app is free to render a feed as one opaque node, or to " +
                "interleave ads and shelves, and then \"the second item\" is genuinely ambiguous. " +
                "Jarvis reports its confidence and asks rather than guessing.",
            remedy = ACCESSIBILITY_REMEDY,
        ) { probe ->
            if (probe.accessibilityServiceEnabled()) CapabilityStatus.PARTIALLY_SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = SCREEN_OCR,
            name = "Reading text from pixels (OCR)",
            group = CapabilityGroup.SCREEN,
            bestCase = CapabilityStatus.MODEL_REQUIRED,
            explanation = "No OCR or computer-vision model is bundled. Jarvis reads the " +
                "accessibility tree instead, which is more accurate than OCR when it is " +
                "available.",
            remedy = "Add an on-device OCR model; the vision port is designed for it.",
        ) { CapabilityStatus.MODEL_REQUIRED },

        capability(
            id = READ_SCREEN_WHILE_LOCKED,
            name = "Reading the screen while locked",
            group = CapabilityGroup.SCREEN,
            bestCase = CapabilityStatus.NOT_POSSIBLE,
            explanation = "Android does not expose app content to accessibility services while " +
                "the secure lock screen is showing.",
            androidLimitation = "The secure lock screen is a platform boundary. Jarvis will not " +
                "attempt to bypass it, and no configuration changes that.",
        ) { CapabilityStatus.NOT_POSSIBLE },

        capability(
            id = MULTI_STEP_AGENT,
            name = "Multi-step tasks",
            group = CapabilityGroup.SCREEN,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Observe, understand, plan, act, verify, continue - with recovery when " +
                "a step fails and an honest report when it cannot finish.",
            remedy = ACCESSIBILITY_REMEDY,
        ) { probe ->
            when {
                !probe.accessibilityServiceEnabled() -> CapabilityStatus.PERMISSION_REQUIRED
                else -> CapabilityStatus.SUPPORTED
            }
        },

        // ====================================================================== voice ==
        capability(
            id = VOICE_INPUT,
            name = "Speech recognition",
            group = CapabilityGroup.VOICE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Uses the platform SpeechRecognizer. Whether recognition happens " +
                "on-device depends entirely on which recogniser the device ships - many only " +
                "offer an online one, and Jarvis reports which it got.",
            remedy = "Grant microphone permission.",
        ) { probe ->
            when {
                !probe.hasMicrophoneHardware() -> CapabilityStatus.DEVICE_DEPENDENT
                !probe.hasSpeechRecognizer() -> CapabilityStatus.DEVICE_DEPENDENT
                !probe.hasPermission(PermissionKind.MICROPHONE) -> CapabilityStatus.PERMISSION_REQUIRED
                else -> CapabilityStatus.PARTIALLY_SUPPORTED
            }
        },

        capability(
            id = VOICE_OUTPUT,
            name = "Speaking responses",
            group = CapabilityGroup.VOICE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Uses the platform text-to-speech engine, and lets you pick the voice, " +
                "pitch and rate.",
        ) { probe ->
            when {
                !probe.hasTtsEngine() -> CapabilityStatus.DEVICE_DEPENDENT
                else -> CapabilityStatus.SUPPORTED
            }
        },

        capability(
            id = WAKE_WORD,
            name = "Wake word",
            group = CapabilityGroup.VOICE,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "Off by default, because it means holding the microphone open in a " +
                "foreground service: a visible ongoing notification and real battery cost. " +
                "Detection is a lightweight on-device matcher, not a trained hotword model, so " +
                "it misses in noise. Android does not allow a third-party app to register a " +
                "system-level hotword.",
            remedy = "Turn it on in Settings > Voice, or use push-to-talk instead.",
            androidLimitation = "System hotword registration is reserved for the OEM assistant.",
        ) { probe ->
            when {
                !probe.hasMicrophoneHardware() -> CapabilityStatus.DEVICE_DEPENDENT
                !probe.hasPermission(PermissionKind.MICROPHONE) -> CapabilityStatus.PERMISSION_REQUIRED
                else -> CapabilityStatus.PARTIALLY_SUPPORTED
            }
        },

        capability(
            id = VOICE_HISTORY,
            name = "Keeping voice transcripts",
            group = CapabilityGroup.VOICE,
            bestCase = CapabilityStatus.DISABLED_BY_USER,
            explanation = "Opt-in. When off, transcripts are used for the current turn and then " +
                "discarded.",
            remedy = "Turn it on in Settings > Privacy.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = VOICE_WHILE_LOCKED,
            name = "Voice interaction while locked",
            group = CapabilityGroup.VOICE,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "A foreground service can listen while the screen is off, and can " +
                "answer with things that do not need private data. Anything sensitive first " +
                "requires the device to be unlocked with its real credential.",
            remedy = "Grant microphone permission and allow the foreground service.",
        ) { probe ->
            when {
                !probe.hasPermission(PermissionKind.MICROPHONE) -> CapabilityStatus.PERMISSION_REQUIRED
                !probe.isIgnoringBatteryOptimizations() -> CapabilityStatus.PARTIALLY_SUPPORTED
                else -> CapabilityStatus.PARTIALLY_SUPPORTED
            }
        },

        // ====================================================================== phone ==
        capability(
            id = OPEN_APP,
            name = "Opening apps",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Resolves any app with a launcher icon by name, tolerating typos, via " +
                "scoped package queries - blanket package visibility is never requested.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = MEDIA_CONTROL,
            name = "Play, pause, skip, resume",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "Media transport controls go through MediaSession, which requires " +
                "notification access to enumerate. Apps that do not publish a media session can " +
                "only be controlled through their on-screen buttons, via the screen agent.",
            remedy = NOTIFICATION_ACCESS_REMEDY,
        ) { probe ->
            if (probe.notificationListenerEnabled()) CapabilityStatus.PARTIALLY_SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = VOLUME_CONTROL,
            name = "Volume control",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Sets media, ring or alarm volume to an absolute level or a percentage.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = SEND_MESSAGE,
            name = "Sending a message",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "Resolves the contact, fills in the body and hands off to your " +
                "messaging app, which performs the actual send. Jarvis does not request SEND_SMS, " +
                "so it cannot send behind your back - and always asks before handing off.",
            remedy = "Grant contacts permission to resolve names to numbers.",
        ) { probe ->
            if (probe.hasPermission(PermissionKind.CONTACTS)) CapabilityStatus.PARTIALLY_SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = READ_MESSAGES,
            name = "Reading your SMS database",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.NOT_POSSIBLE,
            explanation = "Not implemented. Jarvis does not request READ_SMS, because a personal " +
                "assistant that can read every message ever sent is a standing risk that does " +
                "not pay for itself.",
            androidLimitation = "A deliberate design decision, not a platform limit. Reading " +
                "message *notifications* is offered instead.",
        ) { CapabilityStatus.NOT_POSSIBLE },

        capability(
            id = READ_MESSAGE_NOTIFICATIONS,
            name = "Reading incoming message notifications",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "With notification access, Jarvis can read aloud or summarise what just " +
                "arrived, and reply through the notification's own action.",
            remedy = NOTIFICATION_ACCESS_REMEDY,
        ) { probe ->
            if (probe.notificationListenerEnabled()) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = PLACE_CALL,
            name = "Making a call",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "Opens the dialer with the number already filled in. The call is placed " +
                "by you, on the dialer's own button - Jarvis does not request CALL_PHONE and will " +
                "not place calls silently.",
            remedy = "Grant contacts permission to resolve names.",
        ) { probe ->
            if (probe.hasPermission(PermissionKind.CONTACTS)) CapabilityStatus.PARTIALLY_SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = READ_CONTACTS,
            name = "Looking up contacts",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Reads names and numbers locally to resolve \"message John\".",
            remedy = "Grant contacts permission.",
        ) { probe ->
            if (probe.hasPermission(PermissionKind.CONTACTS)) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = READ_NOTIFICATIONS,
            name = "Reading notifications",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "With notification access, notifications can be read aloud and acted " +
                "on - including while the screen is off.",
            remedy = NOTIFICATION_ACCESS_REMEDY,
        ) { probe ->
            if (probe.notificationListenerEnabled()) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = DEVICE_STATUS,
            name = "Battery, charging, connectivity, storage",
            group = CapabilityGroup.PHONE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Read locally, no permission required.",
        ) { CapabilityStatus.SUPPORTED },

        // ================================================================== scheduling ==
        capability(
            id = REMINDERS,
            name = "Reminders",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Understands \"in 20 minutes\", \"at 7pm\", \"tomorrow morning\" and " +
                "\"after dinner\" - the last using your own configured time for dinner.",
            remedy = "Allow exact alarms in Settings for on-the-minute accuracy.",
        ) { probe ->
            when {
                !probe.hasPermission(PermissionKind.NOTIFICATIONS) -> CapabilityStatus.PERMISSION_REQUIRED
                probe.exactAlarmsAllowed() -> CapabilityStatus.SUPPORTED
                else -> CapabilityStatus.PARTIALLY_SUPPORTED
            }
        },

        capability(
            id = ALARMS,
            name = "Alarms",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "A third-party alarm can be silenced by Doze, so Jarvis hands real " +
                "alarms to the system clock app, which is exempt. It says which it did.",
        ) { CapabilityStatus.PARTIALLY_SUPPORTED },

        capability(
            id = TIMERS,
            name = "Timers and countdowns",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Runs in a foreground service, so it survives the screen turning off.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = TASKS_AND_NOTES,
            name = "Tasks and notes",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Stored locally, individually editable and deletable, exportable.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = AUTOMATION_TIME,
            name = "Time-based automations",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "\"At 7pm remind me to study\", \"every Sunday summarise my tasks\".",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = AUTOMATION_BATTERY,
            name = "Battery automations",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "\"When battery falls below 20%, tell me\" - evaluated from the system " +
                "battery broadcast, no permission needed.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = AUTOMATION_HEADSET,
            name = "Headphone automations",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "\"When I connect my headphones, open my music app\". Wired headsets " +
                "need no permission; Bluetooth headsets need Nearby devices permission.",
            remedy = "Grant Nearby devices permission for Bluetooth headsets.",
        ) { probe ->
            if (probe.hasPermission(PermissionKind.BLUETOOTH_CONNECT)) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PARTIALLY_SUPPORTED
        },

        capability(
            id = AUTOMATION_CHARGER,
            name = "Charger automations",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Fires on power connected/disconnected broadcasts.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = AUTOMATION_APP,
            name = "\"When I open an app\" automations",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "Detected from the accessibility window-state events, so it needs the " +
                "screen agent enabled. Android gives third-party apps no background " +
                "usage-events feed.",
            remedy = ACCESSIBILITY_REMEDY,
        ) { probe ->
            if (probe.accessibilityServiceEnabled()) CapabilityStatus.PARTIALLY_SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = AUTOMATION_LOCATION,
            name = "Location automations",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "\"When I arrive at college\" needs location permission. Android forbids " +
                "third-party apps from continuous background location without an ongoing " +
                "foreground service, so Jarvis checks position while it is already awake rather " +
                "than tracking you. Background location permission is never requested.",
            remedy = "Grant location permission.",
        ) { probe ->
            if (probe.hasPermission(PermissionKind.LOCATION)) CapabilityStatus.PARTIALLY_SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = AUTOMATION_NOTIFICATION,
            name = "Notification-triggered automations",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "\"When a message arrives from X, read it aloud\".",
            remedy = NOTIFICATION_ACCESS_REMEDY,
        ) { probe ->
            if (probe.notificationListenerEnabled()) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PERMISSION_REQUIRED
        },

        capability(
            id = AUTOMATION_SURVIVES_REBOOT,
            name = "Automations surviving reboot and Doze",
            group = CapabilityGroup.SCHEDULING,
            bestCase = CapabilityStatus.PARTIALLY_SUPPORTED,
            explanation = "Rules and reminders are re-armed on boot. But Doze defers ordinary " +
                "background work, so a rule may fire late unless the app is exempt from battery " +
                "optimisation - Jarvis reports that instead of implying it always fires on time.",
            remedy = "Disable battery optimisation for Jarvis.",
        ) { probe ->
            if (probe.isIgnoringBatteryOptimizations()) CapabilityStatus.SUPPORTED
            else CapabilityStatus.PARTIALLY_SUPPORTED
        },

        // ========================================================= teaching/knowledge ==
        capability(
            id = TEACH_LOCAL,
            name = "Teaching from your own material",
            group = CapabilityGroup.TEACHING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Assesses your level, explains, gives an example, sets a problem, " +
                "marks it, explains the mistake and adapts difficulty - all offline, and all " +
                "tracked locally.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = TEACH_ONLINE,
            name = "Teaching with an online AI",
            group = CapabilityGroup.TEACHING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Fetches permitted material from an online provider you enabled, then " +
                "builds the lesson locally and caches the material so it works offline later. " +
                "Every such turn is labelled as online.",
            remedy = "Enable online AI in Settings and connect.",
        ) { probe ->
            if (probe.isOnline()) CapabilityStatus.SUPPORTED else CapabilityStatus.ONLINE_REQUIRED
        },

        capability(
            id = LEARNING_PROGRESS,
            name = "Learning progress tracking",
            group = CapabilityGroup.TEACHING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Topics studied, current level, recurring mistakes and mastery, stored " +
                "locally.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = INGEST_PDF_TEXT,
            name = "Ingesting PDFs and documents",
            group = CapabilityGroup.TEACHING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Text is extracted on-device with the platform PdfRenderer, chunked and " +
                "indexed locally. Retrieval afterwards never needs the internet.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = INGEST_SCANNED_PDF,
            name = "Ingesting scanned/image-only PDFs",
            group = CapabilityGroup.TEACHING,
            bestCase = CapabilityStatus.MODEL_REQUIRED,
            explanation = "A scanned PDF has no text layer, and no OCR model is bundled. Jarvis " +
                "reports the document as image-only rather than pretending it read it.",
            remedy = "Add an on-device OCR model.",
        ) { CapabilityStatus.MODEL_REQUIRED },

        capability(
            id = INGEST_WEB_PAGE,
            name = "Saving web pages to your knowledge base",
            group = CapabilityGroup.TEACHING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Fetches a page you ask for, stores it locally and marks it as cached " +
                "online material.",
            remedy = "Enable online features and connect.",
        ) { probe ->
            if (probe.isOnline()) CapabilityStatus.SUPPORTED else CapabilityStatus.ONLINE_REQUIRED
        },

        capability(
            id = KNOWLEDGE_SEARCH,
            name = "Answering from your notes",
            group = CapabilityGroup.TEACHING,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "\"What did the PDF say about Thevenin's theorem?\" retrieves locally " +
                "and attributes the answer to a document, section and page.",
        ) { CapabilityStatus.SUPPORTED },

        // ===================================================================== online ==
        capability(
            id = WEB_SEARCH,
            name = "Web search",
            group = CapabilityGroup.ONLINE,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Off by default. Results are summarised and, if you ask, cached locally.",
            remedy = "Enable online features in Settings.",
        ) { probe ->
            if (probe.isOnline()) CapabilityStatus.SUPPORTED else CapabilityStatus.ONLINE_REQUIRED
        },

        // =================================================================== security ==
        capability(
            id = SECRET_PHRASE,
            name = "Secret assistant phrase",
            group = CapabilityGroup.SECURITY,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Authorises assistant-level actions. The phrase is never stored: only a " +
                "PBKDF2-HMAC-SHA256 verifier with a random salt, kept in Keystore-protected " +
                "storage. It is explicitly NOT a way past the device lock screen.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = BIOMETRIC_AUTH,
            name = "Biometric authorisation",
            group = CapabilityGroup.SECURITY,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Used for genuinely sensitive operations, through the platform's own " +
                "prompt.",
            remedy = "Enrol a fingerprint or face unlock in Android Settings.",
        ) { probe ->
            when (probe.biometricAvailability()) {
                BiometricAvailability.AVAILABLE -> CapabilityStatus.SUPPORTED
                BiometricAvailability.NO_HARDWARE -> CapabilityStatus.DEVICE_DEPENDENT
                BiometricAvailability.NONE_ENROLLED -> CapabilityStatus.PERMISSION_REQUIRED
                BiometricAvailability.TEMPORARILY_UNAVAILABLE -> CapabilityStatus.DEVICE_DEPENDENT
                BiometricAvailability.NOT_SUPPORTED_ON_THIS_API -> CapabilityStatus.DEVICE_DEPENDENT
            }
        },

        capability(
            id = DEVICE_CREDENTIAL_AUTH,
            name = "PIN/pattern/password authorisation",
            group = CapabilityGroup.SECURITY,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "The fallback for sensitive actions when no biometric is available, " +
                "using the platform's own confirm-device-credential screen.",
        ) { probe ->
            if (probe.androidApiLevel() >= 26) CapabilityStatus.SUPPORTED
            else CapabilityStatus.DEVICE_DEPENDENT
        },

        capability(
            id = UNLOCK_SECURE_LOCK_SCREEN,
            name = "Unlocking the device",
            group = CapabilityGroup.SECURITY,
            bestCase = CapabilityStatus.NOT_POSSIBLE,
            explanation = "Jarvis cannot and will not unlock a secure lock screen.",
            androidLimitation = "The secure lock screen is a platform security boundary. Any app " +
                "claiming otherwise is either lying or exploiting a vulnerability.",
        ) { CapabilityStatus.NOT_POSSIBLE },

        capability(
            id = DATA_EXPORT,
            name = "Export everything",
            group = CapabilityGroup.SECURITY,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Memories, conversations, tasks, notes, automations, learning progress " +
                "and configuration, as a single local JSON file.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = DATA_DELETION,
            name = "Delete anything",
            group = CapabilityGroup.SECURITY,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "Per memory, per conversation, per document, or everything at once. No " +
                "account, no server, nothing to request it from.",
        ) { CapabilityStatus.SUPPORTED },

        capability(
            id = NO_SILENT_UPLOAD,
            name = "No silent upload",
            group = CapabilityGroup.SECURITY,
            bestCase = CapabilityStatus.SUPPORTED,
            explanation = "A guarantee rather than a feature: with online features off the app " +
                "makes no network calls at all, and connectivity becoming available never " +
                "triggers a sync. There is no account and no telemetry.",
        ) { CapabilityStatus.SUPPORTED },
    )

    private val byId: Map<String, Capability> = all.associateBy { it.id }

    fun find(id: String): Capability? = byId[id]

    /** Status of one capability, or NOT_POSSIBLE for an id that does not exist. */
    fun status(id: String, probe: DeviceCapabilityProbe): CapabilityStatus =
        byId[id]?.status(probe) ?: CapabilityStatus.NOT_POSSIBLE

    fun report(probe: DeviceCapabilityProbe): List<CapabilityReport> =
        all.map { it.describe(probe) }

    fun byGroup(group: CapabilityGroup, probe: DeviceCapabilityProbe): List<CapabilityReport> =
        report(probe).filter { it.group == group }

    /** Everything currently blocked, grouped by what would unblock it. */
    fun blocked(probe: DeviceCapabilityProbe): Map<CapabilityStatus, List<CapabilityReport>> =
        report(probe)
            .filter { it.status != CapabilityStatus.SUPPORTED }
            .groupBy { it.status }

    /**
     * True when [id] can actually be attempted right now.
     *
     * PARTIALLY_SUPPORTED counts as attemptable: the tool will run and report its own
     * limitation. NOT_POSSIBLE, PERMISSION_REQUIRED, ONLINE_REQUIRED, MODEL_REQUIRED,
     * DEVICE_DEPENDENT and DISABLED_BY_USER do not.
     */
    fun isAttemptable(id: String, probe: DeviceCapabilityProbe): Boolean =
        when (status(id, probe)) {
            CapabilityStatus.SUPPORTED, CapabilityStatus.PARTIALLY_SUPPORTED -> true
            else -> false
        }
}
