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
                keyStreamBuf = (QWords128*)Marshal.AllocHGlobal(KeystreamBufferSize);
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

            public static int ThreadCount { get; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

            public static bool DefaultEnableMultiThreading { get; set; } = false;
            public bool EnableMultiThreading { get; set; } = DefaultEnableMultiThreading;

            Keys128128 keys;
            QWords128 counter;

            const int KsBlockCount = 8;
            const int KeystreamBufferSize = BlockSize * KsBlockCount;

            QWords128* keyStreamBuf;
            int keystreamBufferPos = KeystreamBufferSize;

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
                var len = bs.Len;
                fixed (byte* bytes = &bs.Bytes[pos]) {
                    if (EnableMultiThreading && bs.Len >= 32 * 1024 && ThreadCount > 1) {
                        UpdateMT(bytes, len, ThreadCount);
                    } else {
                        Update(bytes, len, keyStreamBuf, ref keystreamBufferPos, ref counter, keys);
                    }
                }
            }

            private void UpdateMT(byte* bytes, int len, int threadCount)
            {
                var pos = 0;
                // update until end of keystream buffer:
                if (keystreamBufferPos != KeystreamBufferSize)
                    Update(bytes, (pos = KeystreamBufferSize - keystreamBufferPos),
                        keyStreamBuf, ref keystreamBufferPos, ref counter, keys);
                int lenPerThread = (len / threadCount);
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
                        var ksBuffer = stackalloc QWords128[KsBlockCount];
                        var ksPos = KeystreamBufferSize;
                        Update(&bytes[threadPos], lenPerThread,
                            ksBuffer, ref ksPos, ref threadCtr, keys);
                    });
                }
                // update the last part by this thread:
                Update(&bytes[pos], len - pos, keyStreamBuf, ref keystreamBufferPos, ref counter, keys);
                Task.WaitAll(tasks);
            }

            static void Update(byte* bytes, int len,
                QWords128* keyStreamBuf, ref int keystreamBufferPos, ref QWords128 counter, Keys128128 keys)
            {
                while (len > 0) {
                    var remainningKeystream = KeystreamBufferSize - keystreamBufferPos;
                    if (remainningKeystream == 0) {
                        keystreamBufferPos = 0;
                        remainningKeystream = KeystreamBufferSize;
                        var ksb = keyStreamBuf;
                        for (int i = 0; i < KsBlockCount; i++) {
                            ksb[i] = counter;
                            if (++counter.v1 == 0)
                                ++counter.v0;
                            ksb[i] = Cipher.encrypt_128_128(keys, ksb[i]);
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
                                ksb[i] = Cipher.encrypt_64_128(keys, ksb[i]);
                            }
                        }
                        var count = end - pos;
                        count = count < remainningKeystream ? count : remainningKeystream;
                        NaiveUtils.XorBytesUnsafe((byte*)keyStreamBuf + keystreamBufferPos, bytes + pos, count);
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

            public static QWords128 encrypt_128_128(Keys128128 keySchedules, QWords128 plaintext)
            {
                var keys = keySchedules.keys;
                var cv1 = plaintext.v1;
                var cv0 = plaintext.v0;
                // for (int i = 0; i < ROUNDS128128; i++) {
                // var key = keys[i];
                foreach (var key in keys) {
                    const int WORDSIZE = 64;
                    cv1 = (cv1 >> 8) | (cv1 << (WORDSIZE - 8)); // x = ROTR(x, 8)
                    cv1 += cv0;
                    cv1 ^= key;
                    cv0 = (cv0 << 3) | (cv0 >> (WORDSIZE - 3)); // y = ROTL(y, 3)
                    cv0 ^= cv1;
                }
                return new QWords128 { v1 = cv1, v0 = cv0 };
            }

            public static DWords64 encrypt_64_128(uint32[] keySchedules, DWords64 plaintext)
            {
                var cv1 = plaintext.v1;
                var cv0 = plaintext.v0;
                foreach (var item in keySchedules) {
                    const int WORDSIZE = 32;
                    cv1 = (cv1 >> 8) | (cv1 << (WORDSIZE - 8)); // x = ROTR(x, 8)
                    cv1 += cv0;
                    cv1 ^= item;
                    cv0 = (cv0 << 3) | (cv0 >> (WORDSIZE - 3)); // y = ROTL(y, 3)
                    cv0 ^= cv1;
                }
                return new DWords64 { v1 = cv1, v0 = cv0 };
            }
        }
    }
}
