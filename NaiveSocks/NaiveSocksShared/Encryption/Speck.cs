using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using uint64 = System.UInt64;
using uint32 = System.UInt32;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

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
                keyStreamBuf = (QWords128*)Marshal.AllocHGlobal((int)KeystreamBufferSize);
                QWords128 workingKey;
                BytesToWords128(key, out workingKey);
                this.keys = Cipher.getKeySchedules_128_128(workingKey);
            }

            public static Ctr128128 Create(byte[] key) => new Ctr128128(key);

            ~Ctr128128()
            {
                Marshal.FreeHGlobal((IntPtr)keyStreamBuf);
                this.keys.free();
            }

            private static void BytesToWords128(byte[] key, out QWords128 words128)
            {
                fixed (byte* k = key) {
                    words128 = *((QWords128*)k);
                }
            }

            public override int IVLength => BlockSize;

            public static uint ThreadCount { get; } = (uint32)Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

            public static bool DefaultEnableMultiThreading { get; set; } = false;
            public bool EnableMultiThreading { get; set; } = DefaultEnableMultiThreading;

            Keys128128 keys;
            QWords128 counter;

            const uint KsBlockCount = 8;
            const uint KeystreamBufferSize = BlockSize * KsBlockCount;

            QWords128* keyStreamBuf;
            uint keystreamBufferPos = KeystreamBufferSize;

            protected override void IVSetup(byte[] IV)
            {
                if (IV.Length < BlockSize)
                    throw new ArgumentException($"IV.Length < {BlockSize}");
                BytesToWords128(IV, out counter);
            }

            public override void Update(BytesSegment bs)
            {
                bs.CheckAsParameter();
                var pos = (uint)bs.Offset;
                var len = (uint)bs.Len;
                fixed (byte* bytes = &bs.Bytes[pos]) {
                    if (EnableMultiThreading && bs.Len >= 32 * 1024 && ThreadCount > 1) {
                        UpdateMT(bytes, len, ThreadCount);
                    } else {
                        Update(bytes, len, keyStreamBuf, ref keystreamBufferPos, ref counter, keys);
                    }
                }
            }

            private void UpdateMT(byte* bytes, uint len, uint threadCount)
            {
                uint pos = 0;
                // update until end of keystream buffer:
                if (keystreamBufferPos != KeystreamBufferSize)
                    Update(bytes, (pos = KeystreamBufferSize - keystreamBufferPos),
                        keyStreamBuf, ref keystreamBufferPos, ref counter, keys);
                uint lenPerThread = (len / threadCount);
                lenPerThread -= (lenPerThread % KeystreamBufferSize); // align to keystream buffer size
                var ctrPerThread = (ulong)lenPerThread / BlockSize;
                var tasks = new Task[threadCount - 1];
                for (int i = 0; i < tasks.Length; i++) {
                    var threadPos = pos;
                    pos += lenPerThread;
                    var threadCtr = counter;
                    var oldV1 = counter.v1;
                    if ((counter.v1 += ctrPerThread) < oldV1)
                        counter.v0++;
                    tasks[i] = Task.Run(() => {
                        var ksBuffer = stackalloc QWords128[(int)KsBlockCount];
                        var ksPos = KeystreamBufferSize;
                        Update(&bytes[threadPos], lenPerThread,
                            ksBuffer, ref ksPos, ref threadCtr, keys);
                    });
                }
                // update the last part by this thread:
                Update(&bytes[pos], len - pos, keyStreamBuf, ref keystreamBufferPos, ref counter, keys);
                Task.WaitAll(tasks);
            }

            static void Update(byte* bytes, uint len,
                QWords128* keyStreamBuf, ref uint keystreamBufferPos, ref QWords128 counter, Keys128128 keys)
            {
                while (len > 0) {
                    var remainningKeystream = KeystreamBufferSize - keystreamBufferPos;
                    if (remainningKeystream == 0) {
                        keystreamBufferPos = 0;
                        remainningKeystream = KeystreamBufferSize;
                        var ksb = keyStreamBuf;
                        for (uint i = 0; i < KsBlockCount; i += 4) {
                            for (uint j = 0; j < 4; j++) {
                                ksb[i + j] = counter;
                                if (++counter.v1 == 0)
                                    ++counter.v0;
                            }
                            Cipher.encrypt_128_128_4blocks(keys, &ksb[i]);
                        }
                    }
                    var count = len < remainningKeystream ? len : remainningKeystream;
                    NaiveUtils.XorBytesUnsafe((byte*)keyStreamBuf + keystreamBufferPos, bytes, count);
                    bytes += count;
                    len -= count;
                    keystreamBufferPos += count;
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
                keyStreamBuf = (DWords64*)Marshal.AllocHGlobal(KeystreamBufferSize);
                DWords128 workingKey;
                BytesToBlock128(key, out workingKey);
                this.keys = Cipher.getKeySchedules_64_128(workingKey);
            }

            public static Ctr64128 Create(byte[] key) => new Ctr64128(key);

            ~Ctr64128()
            {
                Marshal.FreeHGlobal((IntPtr)keyStreamBuf);
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

            const int KsBlockCount = 16;
            const int KeystreamBufferSize = BlockSize * KsBlockCount;

            DWords64* keyStreamBuf;
            int keystreamBufferPos = KeystreamBufferSize;

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
                var keyStreamBuf = this.keyStreamBuf;
                fixed (byte* bytes = bs.Bytes) {
                    while (pos < end) {
                        var remainningKeystream = KeystreamBufferSize - keystreamBufferPos;
                        if (remainningKeystream == 0) {
                            keystreamBufferPos = 0;
                            remainningKeystream = KeystreamBufferSize;
                            var ksb = keyStreamBuf;
                            for (int i = 0; i < KsBlockCount; i++) {
                                ksb[i] = counter;
                                if (++counter.v1 == 0)
                                    ++counter.v0;
                                Cipher.encrypt_64_128(keys, &ksb[i]);
                            }
                        }
                        var count = end - pos;
                        count = count < remainningKeystream ? count : remainningKeystream;
                        NaiveUtils.XorBytesUnsafe((byte*)keyStreamBuf + keystreamBufferPos, bytes + pos, (uint32)count);
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

        public struct Keys128128
        {
            public uint64[] keys;

            public static Keys128128 alloc()
            {
                return new Keys128128 { keys = new uint64[32] };
            }

            public void free()
            {
            }
        }

        static unsafe class Cipher
        {
            // Reference: https://github.com/dimview/speck_cipher/blob/master/speck_128_128.cpp

            const int ROUNDS128128 = 32;
            const int ROUNDS64128 = 27;

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

            public static Keys128128 getKeySchedules_128_128(QWords128 key)
            {
                var r = Keys128128.alloc();
                var keys = r.keys;
                keys[0] = key.v0;
                for (uint64 i = 0; i < ROUNDS128128 - 1; i++) {
                    speck_round_64(ref key.v1, ref key.v0, i);
                    keys[1 + i] = key.v0;
                }
                return r;
            }

            public static uint32[] getKeySchedules_64_128(DWords128 key)
            {
                var keys = new uint32[ROUNDS64128];
                keys[0] = key.v0;
                var a = stackalloc uint32[3];
                a[0] = key.v1;
                a[1] = key.v2;
                a[2] = key.v3;
                for (uint32 i = 0; i < ROUNDS64128 - 1; i++) {
                    speck_round_32(ref a[i % 3], ref key.v0, i);
                    keys[1 + i] = key.v0;
                }
                return keys;
            }

            public static void encrypt_128_128_4blocks(Keys128128 keySchedules, QWords128* plaintext)
            {
                var keys = keySchedules.keys;
                uint64 v01 = plaintext[0].v1, v00 = plaintext[0].v0;
                uint64 v11 = plaintext[1].v1, v10 = plaintext[1].v0;
                uint64 v21 = plaintext[2].v1, v20 = plaintext[2].v0;
                uint64 v31 = plaintext[3].v1, v30 = plaintext[3].v0;
                foreach (var key in keys) {
                    const int WORDSIZE = 64;
                    v01 = (v01 >> 8) | (v01 << (WORDSIZE - 8)); // x = ROTR(x, 8)
                    v01 += v00;
                    v01 ^= key;
                    v00 = (v00 << 3) | (v00 >> (WORDSIZE - 3)); // y = ROTL(y, 3)
                    v00 ^= v01;

                    v11 = (v11 >> 8) | (v11 << (WORDSIZE - 8));
                    v11 += v10;
                    v11 ^= key;
                    v10 = (v10 << 3) | (v10 >> (WORDSIZE - 3));
                    v10 ^= v11;

                    v21 = (v21 >> 8) | (v21 << (WORDSIZE - 8));
                    v21 += v20;
                    v21 ^= key;
                    v20 = (v20 << 3) | (v20 >> (WORDSIZE - 3));
                    v20 ^= v21;

                    v31 = (v31 >> 8) | (v31 << (WORDSIZE - 8));
                    v31 += v30;
                    v31 ^= key;
                    v30 = (v30 << 3) | (v30 >> (WORDSIZE - 3));
                    v30 ^= v31;
                }
                plaintext[0].v1 = v01; plaintext[0].v0 = v00;
                plaintext[1].v1 = v11; plaintext[1].v0 = v10;
                plaintext[2].v1 = v21; plaintext[2].v0 = v20;
                plaintext[3].v1 = v31; plaintext[3].v0 = v30;
            }

            public static void encrypt_64_128(uint32[] keySchedules, DWords64* plaintext)
            {
                var cv1 = plaintext->v1;
                var cv0 = plaintext->v0;
                foreach (var item in keySchedules) {
                    const int WORDSIZE = 32;
                    cv1 = (cv1 >> 8) | (cv1 << (WORDSIZE - 8)); // x = ROTR(x, 8)
                    cv1 += cv0;
                    cv1 ^= item;
                    cv0 = (cv0 << 3) | (cv0 >> (WORDSIZE - 3)); // y = ROTL(y, 3)
                    cv0 ^= cv1;
                }
                plaintext->v1 = cv1;
                plaintext->v0 = cv0;
            }
        }
    }
}
