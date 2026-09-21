package dev.jarvis.core.util

import dev.jarvis.core.testing.Suite

object JsonSuite : Suite("util/json") {
    init {
        test("writes primitives") {
            dev.jarvis.core.testing.assertEquals("null", JsonNull.encode())
            dev.jarvis.core.testing.assertEquals("true", JsonBool(true).encode())
            dev.jarvis.core.testing.assertEquals("\"hi\"", JsonString("hi").encode())
            dev.jarvis.core.testing.assertEquals("42", JsonNumber(42.0).encode())
            dev.jarvis.core.testing.assertEquals("1.5", JsonNumber(1.5).encode())
        }

        test("escapes control characters and quotes") {
            val encoded = JsonString("say \"hi\"\nline2\ttabbed\\back").encode()
            dev.jarvis.core.testing.assertEquals(
                "\"say \\\"hi\\\"\\nline2\\ttabbed\\\\back\"",
                encoded,
            )
        }

        test("writes nested structures") {
            val value = jsonObject(
                "nickname" to json("Jarvis"),
                "volume" to json(40),
                "tags" to jsonArray(json("ece"), json("robotics")),
                "nested" to jsonObject("online" to json(false)),
            )
            val encoded = value.encode()
            dev.jarvis.core.testing.assertContains(encoded, "\"nickname\":\"Jarvis\"")
            dev.jarvis.core.testing.assertContains(encoded, "\"tags\":[\"ece\",\"robotics\"]")
            dev.jarvis.core.testing.assertContains(encoded, "\"online\":false")
        }

        test("round-trips an object") {
            val original = jsonObject(
                "id" to json("mem-1"),
                "text" to json("Working on an ECE robotics project"),
                "weight" to json(0.75),
                "pinned" to json(true),
                "aliases" to jsonArray(json("robotics"), json("ece")),
            )
            val parsed = parseJson(original.encode()) as JsonObject
            dev.jarvis.core.testing.assertEquals("mem-1", parsed.string("id"))
            dev.jarvis.core.testing.assertEquals(
                "Working on an ECE robotics project",
                parsed.string("text"),
            )
            dev.jarvis.core.testing.assertEquals(true, parsed.bool("pinned"))
            dev.jarvis.core.testing.assertEquals(listOf("robotics", "ece"), parsed.stringList("aliases"))
            dev.jarvis.core.testing.assertEquals(0.75, parsed.double("weight"))
        }

        test("parses whitespace-heavy and empty containers") {
            val parsed = parseJson("  {  \"a\" : [ ] , \"b\" : {  } }  ") as JsonObject
            dev.jarvis.core.testing.assertEquals(0, parsed.arr("a")?.size)
            dev.jarvis.core.testing.assertEquals(0, parsed.obj("b")?.entries?.size)
        }

        test("parses unicode escapes and negative/exponent numbers") {
            val parsed = parseJson("{\"u\":\"\\u0041\\u00e9\",\"n\":-12,\"e\":1.5e3}") as JsonObject
            dev.jarvis.core.testing.assertEquals("A\u00E9", parsed.string("u"))
            dev.jarvis.core.testing.assertEquals(-12, parsed.int("n"))
            dev.jarvis.core.testing.assertEquals(1500.0, parsed.double("e"))
        }

        test("rejects malformed input instead of guessing") {
            dev.jarvis.core.testing.assertNull(parseJsonOrNull("{\"a\":}"))
            dev.jarvis.core.testing.assertNull(parseJsonOrNull("[1,2"))
            dev.jarvis.core.testing.assertNull(parseJsonOrNull("{'single':1}"))
            dev.jarvis.core.testing.assertNull(parseJsonOrNull("{\"a\":1} trailing"))
            dev.jarvis.core.testing.assertNull(parseJsonOrNull(""))
            dev.jarvis.core.testing.assertNull(parseJsonOrNull(null))
        }

        test("parseJson throws a positioned exception") {
            val message = dev.jarvis.core.testing.assertThrows { parseJson("{\"a\":1,}") }
            dev.jarvis.core.testing.assertTrue(message.contains("offset"), "message was: $message")
        }
    }
}
