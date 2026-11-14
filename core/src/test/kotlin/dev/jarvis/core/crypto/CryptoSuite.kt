package dev.jarvis.core.crypto

import dev.jarvis.core.testing.Suite
import dev.jarvis.core.testing.assertEquals
import dev.jarvis.core.testing.assertFalse
import dev.jarvis.core.testing.assertTrue

/**
 * Cryptographic correctness.
 *
 * Every expected value below was generated with Python's hashlib/hmac (an independent,
 * authoritative implementation), not copied from memory. That matters: a hashing routine
 * that agrees with itself proves nothing, and the secret-phrase verifier is the one place
 * where a subtly wrong digest would be invisible in normal use.
 */
object CryptoSuite : Suite("crypto") {
    init {
        test("sha256 matches reference vectors") {
            assertEquals(
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                Sha256.digestHex(""),
                "empty input",
            )
            assertEquals(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                Sha256.digestHex("abc"),
                "FIPS 180-2 'abc' vector",
            )
            assertEquals(
                "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
                Sha256.digestHex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"),
                "FIPS 180-2 two-block vector",
            )
        }

        test("sha256 handles every padding boundary") {
            // 55, 56, 63, 64 and 65 bytes each exercise a different padding path: whether
            // the length word fits in the final block, and whether an extra block is needed.
            assertEquals(
                "9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318",
                Sha256.digestHex("a".repeat(55)),
            )
            assertEquals(
                "b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a",
                Sha256.digestHex("a".repeat(56)),
            )
            assertEquals(
                "7d3e74a05d7db15bce4ad9ec0658ea98e3f06eeecf16b4c6fff2da457ddc2f34",
                Sha256.digestHex("a".repeat(63)),
            )
            assertEquals(
                "ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb",
                Sha256.digestHex("a".repeat(64)),
            )
            assertEquals(
                "635361c48bb9eab14198e76ea8ab7f1a41685d6ad62aa9146d301d4f17eb0ae0",
                Sha256.digestHex("a".repeat(65)),
            )
            assertEquals(
                "c2a908d98f5df987ade41b5fce213067efbcc21ef2240212a41e54b5e7c28ae5",
                Sha256.digestHex("a".repeat(200)),
                "multi-block",
            )
        }

        test("sha256 encodes utf-8 multi-byte input correctly") {
            assertEquals(
                "5f43d1ea5cce7aa4f7e9300dabb18356c4b8d46cb9b91688306e38b26201bc50",
                Sha256.digestHex("jarvis-secret-phrase"),
            )
        }

        test("hmac-sha256 matches reference vectors") {
            assertEquals(
                "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
                HmacSha256.macHex(
                    "key".encodeToByteArray(),
                    "The quick brown fox jumps over the lazy dog".encodeToByteArray(),
                ),
            )
            // A key longer than the 64-byte block must be hashed first.
            assertEquals(
                "393355db8f0edbf0064ede0eed08a3ee53731f45398569d3c2c33edfebd5b52b",
                HmacSha256.macHex("x".repeat(100).encodeToByteArray(), "message".encodeToByteArray()),
            )
            assertEquals(
                "8bb990c40a7d61cb97597a942125025be50ac8beb74436e3735b98893a7f6620",
                HmacSha256.macHex("k".encodeToByteArray(), ByteArray(0)),
            )
        }

        test("pbkdf2-hmac-sha256 matches reference vectors") {
            assertEquals(
                "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b",
                Pbkdf2.deriveHex("password", "salt".encodeToByteArray(), 1, 32),
            )
            assertEquals(
                "ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43",
                Pbkdf2.deriveHex("password", "salt".encodeToByteArray(), 2, 32),
            )
            assertEquals(
                "c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a",
                Pbkdf2.deriveHex("password", "salt".encodeToByteArray(), 4096, 32),
            )
        }

        test("pbkdf2 produces longer output across multiple blocks") {
            // 48 bytes requires two HMAC blocks, exercising the block-index counter.
            assertEquals(
                "632c2812e46d4604102ba7618e9d6d7d2f8128f6266b4a03264d2a0460b7dcb3" +
                    "88b3b1131f741bcbeb02541c8c2e97bd",
                Pbkdf2.deriveHex("password", "salt".encodeToByteArray(), 1000, 48),
            )
        }

        test("pbkdf2 is deterministic and salt sensitive") {
            val phrase = "assistant, authorization phrase"
            val saltA = byteArrayOf(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88.toByte())
            val saltB = byteArrayOf(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x89.toByte())
            val first = Pbkdf2.deriveHex(phrase, saltA, 10_000, 32)
            val second = Pbkdf2.deriveHex(phrase, saltA, 10_000, 32)
            val other = Pbkdf2.deriveHex(phrase, saltB, 10_000, 32)
            assertEquals(first, second, "same inputs must give the same verifier")
            assertTrue(first != other, "a one-bit salt change must change the verifier")
            assertEquals(
                "61a210587c51c71ec2b68acd375f338bb058ab217587f06882d17436779aca74",
                first,
            )
        }

        test("constantTimeEquals compares by content, not reference") {
            val a = byteArrayOf(1, 2, 3, 4)
            val b = byteArrayOf(1, 2, 3, 4)
            val c = byteArrayOf(1, 2, 3, 5)
            val d = byteArrayOf(1, 2, 3)
            assertTrue(constantTimeEquals(a, b))
            assertFalse(constantTimeEquals(a, c))
            assertFalse(constantTimeEquals(a, d), "different lengths must not be equal")
            assertTrue(constantTimeEquals(ByteArray(0), ByteArray(0)))
        }

        test("hex round-trips") {
            val bytes = byteArrayOf(0x00, 0x0f, 0x7f, 0x80.toByte(), 0xff.toByte())
            val hex = toHex(bytes)
            assertEquals("000f7f80ff", hex)
            assertTrue(fromHex(hex).contentEquals(bytes))
        }

        test("random bytes port honours requested length") {
            val fixed = FixedRandomBytes()
            assertEquals(16, fixed.nextBytes(16).size)
            assertEquals(0, fixed.nextBytes(0).size)
            assertTrue(fixed.nextBytes(8).contentEquals(fixed.nextBytes(8)), "fixed source is deterministic")
        }
    }
}
