using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using uint64 = System.UInt64;
using uint32 = System.UInt32;

namespace NaiveSocks
{

    // NOTE:
    // This is an incorrect implementation. 
    // It does not use big-endian words, makes incompatible with other implementation.
    // See: https://www.reddit.com/r/crypto/comments/4cjgat/c_implementation_for_speck128256/d1knkiz/

    public static unsafe class Speck
    {
        public class Ctr128128 : IVEncryptorBase
        {
            const int BlockSize = 16;
            const int KeySize = 16;

            public Ctr128128(byte[] key)
            {
                if (key.Length < KeySize)
                    throw new ArgumentException($"key.Length < {KeySize}");
                QWords128 keywords;
                BytesToWords128(key, out keywords);
                this.keys = Cipher.getKeySchedules_128_128(keywords);
            }

            private static void BytesToWords128(byte[] key, out QWords128 words128)
            {
                fixed (byte* k = key) {
                    words128 = *((QWords128*)k);
                }
            }

            public override int IVLength => BlockSize;

            //Block128 realkey;
            uint64[] keys;
            QWords128 counter;
            
            const int KsBlockCount = 8;
            const int KeystreamBufferSize = BlockSize * KsBlockCount;
            KeyStreamBuf keyStreamBuf;
            int keystreamBufferPos = KeystreamBufferSize;

            struct KeyStreamBuf
            {
                public fixed byte bytes[KeystreamBufferSize];
            }

            protected override void IVSetup(byte[] IV)
            {
                if (IV.Length < BlockSize)
                    throw new ArgumentException($"IV.Length < {BlockSize}");
                BytesToWords128(IV, out counter);
            }

            public override void Update(BytesSegment bs)
            {
                bs.CheckAsParameter();
                var pos = bs.Offset;
                var end = pos + bs.Len;
                fixed (byte* keyStreamBuf = this.keyStreamBuf.bytes)
                fixed (byte* bytes = bs.Bytes) {
                    while (pos < end) {
                        var remainningKeystream = KeystreamBufferSize - keystreamBufferPos;
                        if (remainningKeystream == 0) {
                            keystreamBufferPos = 0;
                            remainningKeystream = KeystreamBufferSize;
                            var ksb = (QWords128*)keyStreamBuf;
                            for (int i = 0; i < KsBlockCount; i++) {
                                ksb[i] = counter;
                                if (++counter.v1 == 0)
                                    ++counter.v0;
                                Cipher.encrypt_128_128(keys, ref ksb[i]);
                            }
                        }
                        var count = end - pos;
                        count = count < remainningKeystream ? count : remainningKeystream;
                        NaiveUtils.XorBytesUnsafe(keyStreamBuf + keystreamBufferPos, bytes + pos, count);
                        pos += count;
                        keystreamBufferPos += count;
                    }
                }
            }
        }

        public class Ctr64128 : IVEncryptorBase
        {
            const int BlockSize = 8;
            const int KeySize = 16;

            public Ctr64128(byte[] key)
            {
                if (key.Length < KeySize)
                    throw new ArgumentException($"key.Length < {KeySize}");
                DWords128 keywords;
                BytesToBlock128(key, out keywords);
                this.keys = Cipher.getKeySchedules_64_128(keywords);
            }

            private static void BytesToBlock128(byte[] key, out DWords128 kb128)
            {
                fixed (byte* k = key) {
                    kb128 = *((DWords128*)k);
                }
            }

            private static void BytesToBlock64(byte[] key, out DWords64 kb128)
            {
                fixed (byte* k = key) {
                    kb128 = *((DWords64*)k);
                }
            }

            public override int IVLength => BlockSize;
            
            uint32[] keys;
            DWords64 counter;

            const int KsBlockCount = 8;
            const int KeystreamBufferSize = BlockSize * KsBlockCount;
            KeyStreamBuf keyStreamBuf;
            int keystreamBufferPos = KeystreamBufferSize;

            struct KeyStreamBuf
            {
                public fixed byte bytes[KeystreamBufferSize];
            }

            protected override void IVSetup(byte[] IV)
            {
                if (IV.Length < BlockSize)
                    throw new ArgumentException($"IV.Length < {BlockSize}");
                BytesToBlock64(IV, out counter);
            }

            public override void Update(BytesSegment bs)
            {
                bs.CheckAsParameter();
                var pos = bs.Offset;
                var end = pos + bs.Len;
                fixed (byte* keyStreamBuf = this.keyStreamBuf.bytes)
                fixed (byte* bytes = bs.Bytes) {
                    while (pos < end) {
                        var remainningKeystream = KeystreamBufferSize - keystreamBufferPos;
                        if (remainningKeystream == 0) {
                            keystreamBufferPos = 0;
                            remainningKeystream = KeystreamBufferSize;
                            var ksb = (DWords64*)keyStreamBuf;
                            for (int i = 0; i < KsBlockCount; i++) {
                                ksb[i] = counter;
                                if (++counter.v1 == 0)
                                    ++counter.v0;
                                Cipher.encrypt_64_128(keys, ref ksb[i]);
                            }
                        }
                        var count = end - pos;
                        count = count < remainningKeystream ? count : remainningKeystream;
                        NaiveUtils.XorBytesUnsafe(keyStreamBuf + keystreamBufferPos, bytes + pos, count);
                        pos += count;
                        keystreamBufferPos += count;
                    }
                }
            }
        }

        public struct QWords128
        {
            public uint64 v1, v0;
        }

        public struct DWords128
        {
            public uint32 v3, v2, v1, v0;
        }

        public struct DWords64
        {
            public uint32 v1, v0;
        }

        static unsafe class Cipher
        {
            // Reference: https://github.com/dimview/speck_cipher/blob/master/speck_128_128.cpp

            const uint64 ROUNDS128128 = 32;
            const uint32 ROUNDS64128 = 27;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void speck_round_64(ref uint64 x, ref uint64 y, uint64 k)
            {
                const int WORDSIZE = 64;
                x = (x >> 8) | (x << (WORDSIZE - 8)); // x = ROTR(x, 8)
                x += y;
                x ^= k;
                y = (y << 3) | (y >> (WORDSIZE - 3)); // y = ROTL(y, 3)
                y ^= x;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void speck_round_32(ref uint32 x, ref uint32 y, uint32 k)
            {
                const int WORDSIZE = 32;
                x = (x >> 8) | (x << (WORDSIZE - 8)); // x = ROTR(x, 8)
                x += y;
                x ^= k;
                y = (y << 3) | (y >> (WORDSIZE - 3)); // y = ROTL(y, 3)
                y ^= x;
            }


            // Generate key schedule and encrypt at the same time
            public static void encrypt_128_128(QWords128 key, ref QWords128 ciphertext)
            {
                speck_round_64(ref ciphertext.v1, ref ciphertext.v0, key.v0);
                for (uint64 i = 0; i < ROUNDS128128 - 1; i++) {
                    speck_round_64(ref key.v1, ref key.v0, i);
                    speck_round_64(ref ciphertext.v1, ref ciphertext.v0, key.v0);
                }
            }

            public static uint64[] getKeySchedules_128_128(QWords128 key)
            {
                var keys = new uint64[ROUNDS128128];
                keys[0] = key.v0;
                for (uint64 i = 0; i < ROUNDS128128 - 1; i++) {
                    speck_round_64(ref key.v1, ref key.v0, i);
                    keys[1 + i] = key.v0;
                }
                return keys;
            }

            public static uint32[] getKeySchedules_64_128(DWords128 key)
            {
                var keys = new uint32[ROUNDS128128];
                keys[0] = key.v0;
                var a = stackalloc uint32[3];
                a[0] = key.v1;
                a[1] = key.v2;
                a[2] = key.v3;
                for (uint32 i = 0; i < ROUNDS128128 - 1; i++) {
                    speck_round_32(ref a[i % 3], ref key.v0, i);
                    keys[1 + i] = key.v0;
                }
                return keys;
            }

            public static void encrypt_128_128(uint64[] keySchedules, ref QWords128 ciphertext)
            {
                foreach (var item in keySchedules) {
                    // inlined:
                    //speck_round_64(ref ciphertext.v1, ref ciphertext.v0, item);
                    ciphertext.v1 = (ciphertext.v1 >> 8) | (ciphertext.v1 << (64 - 8)); // x = ROTR(x, 8)
                    ciphertext.v1 += ciphertext.v0;
                    ciphertext.v1 ^= item;
                    ciphertext.v0 = (ciphertext.v0 << 3) | (ciphertext.v0 >> (64 - 3)); // y = ROTL(y, 3)
                    ciphertext.v0 ^= ciphertext.v1;
                }
            }

            public static void encrypt_64_128(uint32[] keySchedules, ref DWords64 ciphertext)
            {
                foreach (var item in keySchedules) {
                    // inlined:
                    // speck_round_32(ref ciphertext.v1, ref ciphertext.v0, item);
                    const int WORDSIZE = 32;
                    ciphertext.v1 = (ciphertext.v1 >> 8) | (ciphertext.v1 << (WORDSIZE - 8)); // x = ROTR(x, 8)
                    ciphertext.v1 += ciphertext.v0;
                    ciphertext.v1 ^= item;
                    ciphertext.v0 = (ciphertext.v0 << 3) | (ciphertext.v0 >> (WORDSIZE - 3)); // y = ROTL(y, 3)
                    ciphertext.v0 ^= ciphertext.v1;
                }
            }

            //int main(void)
            //{
            //    uint64_t plaintext[2] = { 0x7469206564616d20ULL, 0x6c61766975716520ULL };
            //    uint64_t key[2] = { 0x0706050403020100ULL, 0x0f0e0d0c0b0a0908ULL };
            //    uint64_t ciphertext[2];

            //    speck_encrypt(plaintext, key, ciphertext);

            //    printf("Plaintext:  0x%016llx 0x%016llx\n", plaintext[0], plaintext[1]);
            //    printf("Key:        0x%016llx 0x%016llx\n", key[0], key[1]);
            //    printf("Ciphertext: 0x%016llx 0x%016llx\n", ciphertext[0], ciphertext[1]);
            //    printf("Expected:   0x7860fedf5c570d18 0xa65d985179783265\n");
            //    return 0;
            //}
        }
    }
}
