package dev.jarvis.core.util

/**
 * A tiny, dependency-free JSON model + parser + writer.
 *
 * `:core` may not depend on org.json, Gson, Moshi or kotlinx.serialization (see
 * core/build.gradle.kts). The assistant needs JSON for tool-call parameters, config
 * blobs, data export and knowledge-base metadata, so a small correct implementation
 * lives here instead. It handles the full RFC 8259 grammar that the app produces and
 * consumes: objects, arrays, strings (with escapes and \uXXXX), numbers, booleans,
 * null, and arbitrary nesting/whitespace.
 */
sealed class JsonValue {
    open fun encode(): String = JsonWriter.write(this)
    override fun toString(): String = encode()
}

data class JsonObject(val entries: Map<String, JsonValue> = emptyMap()) : JsonValue() {
    operator fun get(key: String): JsonValue? = entries[key]
    fun string(key: String): String? = (entries[key] as? JsonString)?.value
    fun int(key: String): Int? = (entries[key] as? JsonNumber)?.value?.toInt()
    fun long(key: String): Long? = (entries[key] as? JsonNumber)?.value?.toLong()
    fun double(key: String): Double? = (entries[key] as? JsonNumber)?.value
    fun bool(key: String): Boolean? = (entries[key] as? JsonBool)?.value
    fun obj(key: String): JsonObject? = entries[key] as? JsonObject
    fun arr(key: String): JsonArray? = entries[key] as? JsonArray
    fun stringList(key: String): List<String> =
        arr(key)?.items?.mapNotNull { (it as? JsonString)?.value } ?: emptyList()
}

data class JsonArray(val items: List<JsonValue> = emptyList()) : JsonValue() {
    val size: Int get() = items.size
    operator fun get(index: Int): JsonValue? = items.getOrNull(index)
    fun strings(): List<String> = items.mapNotNull { (it as? JsonString)?.value }
}

data class JsonString(val value: String) : JsonValue()
data class JsonNumber(val value: Double) : JsonValue()
data class JsonBool(val value: Boolean) : JsonValue()
data object JsonNull : JsonValue()

class JsonParseException(message: String, val position: Int) :
    RuntimeException("$message (at offset $position)")

/** Convenience constructors. */
fun json(value: String?): JsonValue = if (value == null) JsonNull else JsonString(value)
fun json(value: Int?): JsonValue = if (value == null) JsonNull else JsonNumber(value.toDouble())
fun json(value: Long?): JsonValue = if (value == null) JsonNull else JsonNumber(value.toDouble())
fun json(value: Double?): JsonValue = if (value == null) JsonNull else JsonNumber(value)
fun json(value: Boolean?): JsonValue = if (value == null) JsonNull else JsonBool(value)
fun jsonArray(vararg items: JsonValue): JsonArray = JsonArray(items.toList())
fun jsonObject(vararg pairs: Pair<String, JsonValue?>): JsonObject =
    JsonObject(pairs.filter { it.second != null }.map { it.first to (it.second ?: JsonNull) }.toMap())

/** Parses [text]; returns null instead of throwing when the input is blank or invalid. */
fun parseJsonOrNull(text: String?): JsonValue? =
    if (text.isNullOrBlank()) null else try {
        JsonParser(text).parseDocument()
    } catch (_: JsonParseException) {
        null
    } catch (_: RuntimeException) {
        null
    }

fun parseJson(text: String): JsonValue = JsonParser(text).parseDocument()

object JsonWriter {
    fun write(value: JsonValue): String = buildString { appendValue(this, value) }

    private fun appendValue(sb: StringBuilder, value: JsonValue) {
        when (value) {
            is JsonNull -> sb.append("null")
            is JsonBool -> sb.append(if (value.value) "true" else "false")
            is JsonNumber -> sb.append(formatNumber(value.value))
            is JsonString -> appendString(sb, value.value)
            is JsonArray -> {
                sb.append('[')
                value.items.forEachIndexed { index, item ->
                    if (index > 0) sb.append(',')
                    appendValue(sb, item)
                }
                sb.append(']')
            }
            is JsonObject -> {
                sb.append('{')
                var first = true
                for ((key, item) in value.entries) {
                    if (!first) sb.append(',')
                    first = false
                    appendString(sb, key)
                    sb.append(':')
                    appendValue(sb, item)
                }
                sb.append('}')
            }
        }
    }

    private fun formatNumber(value: Double): String =
        if (value.isNaN() || value.isInfinite()) "null"
        else if (value == value.toLong().toDouble() && kotlin.math.abs(value) < 1e15) value.toLong().toString()
        else value.toString()

    private fun appendString(sb: StringBuilder, raw: String) {
        sb.append('"')
        for (ch in raw) {
            when (ch) {
                '"' -> sb.append("\\\"")
                '\\' -> sb.append("\\\\")
                '\n' -> sb.append("\\n")
                '\r' -> sb.append("\\r")
                '\t' -> sb.append("\\t")
                '\b' -> sb.append("\\b")
                '\u000C' -> sb.append("\\f")
                else -> if (ch < ' ') sb.append("\\u%04x".format(ch.code)) else sb.append(ch)
            }
        }
        sb.append('"')
    }
}

class JsonParser(private val input: String) {
    private var pos = 0

    fun parseDocument(): JsonValue {
        skipWhitespace()
        val value = parseValue()
        skipWhitespace()
        if (pos != input.length) throw JsonParseException("trailing content after JSON value", pos)
        return value
    }

    private fun parseValue(): JsonValue {
        skipWhitespace()
        if (pos >= input.length) throw JsonParseException("unexpected end of input", pos)
        return when (val ch = input[pos]) {
            '{' -> parseObject()
            '[' -> parseArray()
            '"' -> JsonString(parseString())
            't' -> parseLiteral("true", JsonBool(true))
            'f' -> parseLiteral("false", JsonBool(false))
            'n' -> parseLiteral("null", JsonNull)
            else -> if (ch == '-' || ch.isDigit()) parseNumber()
            else throw JsonParseException("unexpected character '$ch'", pos)
        }
    }

    private fun parseLiteral(literal: String, value: JsonValue): JsonValue {
        if (!input.startsWith(literal, pos)) throw JsonParseException("invalid literal", pos)
        pos += literal.length
        return value
    }

    private fun parseObject(): JsonObject {
        expect('{')
        val entries = LinkedHashMap<String, JsonValue>()
        skipWhitespace()
        if (peek() == '}') { pos++; return JsonObject(entries) }
        while (true) {
            skipWhitespace()
            val key = parseString()
            skipWhitespace()
            expect(':')
            val value = parseValue()
            entries[key] = value
            skipWhitespace()
            when (peek()) {
                ',' -> pos++
                '}' -> { pos++; return JsonObject(entries) }
                else -> throw JsonParseException("expected ',' or '}'", pos)
            }
        }
    }

    private fun parseArray(): JsonArray {
        expect('[')
        val items = mutableListOf<JsonValue>()
        skipWhitespace()
        if (peek() == ']') { pos++; return JsonArray(items) }
        while (true) {
            items += parseValue()
            skipWhitespace()
            when (peek()) {
                ',' -> pos++
                ']' -> { pos++; return JsonArray(items) }
                else -> throw JsonParseException("expected ',' or ']'", pos)
            }
        }
    }

    private fun parseString(): String {
        expect('"')
        val sb = StringBuilder()
        while (true) {
            if (pos >= input.length) throw JsonParseException("unterminated string", pos)
            when (val ch = input[pos++]) {
                '"' -> return sb.toString()
                '\\' -> {
                    if (pos >= input.length) throw JsonParseException("unterminated escape", pos)
                    when (val esc = input[pos++]) {
                        '"' -> sb.append('"')
                        '\\' -> sb.append('\\')
                        '/' -> sb.append('/')
                        'n' -> sb.append('\n')
                        'r' -> sb.append('\r')
                        't' -> sb.append('\t')
                        'b' -> sb.append('\b')
                        'f' -> sb.append('\u000C')
                        'u' -> {
                            if (pos + 4 > input.length) throw JsonParseException("truncated \\u escape", pos)
                            val hex = input.substring(pos, pos + 4)
                            val code = hex.toIntOrNull(16)
                                ?: throw JsonParseException("invalid \\u escape '$hex'", pos)
                            sb.append(code.toChar())
                            pos += 4
                        }
                        else -> throw JsonParseException("invalid escape '\\$esc'", pos)
                    }
                }
                else -> sb.append(ch)
            }
        }
    }

    private fun parseNumber(): JsonNumber {
        val start = pos
        if (peek() == '-') pos++
        while (pos < input.length && input[pos].isDigit()) pos++
        if (pos < input.length && input[pos] == '.') {
            pos++
            while (pos < input.length && input[pos].isDigit()) pos++
        }
        if (pos < input.length && (input[pos] == 'e' || input[pos] == 'E')) {
            pos++
            if (pos < input.length && (input[pos] == '+' || input[pos] == '-')) pos++
            while (pos < input.length && input[pos].isDigit()) pos++
        }
        val raw = input.substring(start, pos)
        val value = raw.toDoubleOrNull() ?: throw JsonParseException("invalid number '$raw'", start)
        return JsonNumber(value)
    }

    private fun peek(): Char =
        if (pos < input.length) input[pos] else throw JsonParseException("unexpected end of input", pos)

    private fun expect(ch: Char) {
        if (pos >= input.length || input[pos] != ch) {
            throw JsonParseException("expected '$ch'", pos)
        }
        pos++
    }

    private fun skipWhitespace() {
        while (pos < input.length && input[pos].isWhitespace()) pos++
    }
}
