using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using uint64_t = System.UInt64;
using uint32_t = System.UInt32;

namespace NaiveSocks
{
    unsafe class SpeckCtr128128 : IVEncryptorBase
    {
        public SpeckCtr128128(byte[] key)
        {
            if (key.Length < 16)
                throw new ArgumentException("key.Length < 16");
            Block128 kb128;
            BytesToBlock128(key, out kb128);
            //this.realkey = kb128;
            this.keys = Cipher.getKeySchedules_128_128(kb128);
        }

        private static void BytesToBlock128(byte[] key, out Block128 kb128)
        {
            fixed (byte* k = key) {
                kb128 = *((Block128*)k);
            }
        }

        public override int IVLength => 16;

        //Block128 realkey;
        uint64_t[] keys;
        Block128 counter;

        const int BlockSize = 16;
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
            if (IV.Length < 16)
                throw new ArgumentException("IV.Length < 16");
            BytesToBlock128(IV, out counter);
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
                        var ksb = (Block128*)keyStreamBuf;
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

        public struct Block128
        {
            public uint64_t v1, v0;
        }

        static unsafe class Cipher
        {

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void speck_round_64(ref uint64_t x, ref uint64_t y, uint64_t k)
            {
                x = (x >> 8) | (x << (64 - 8)); // x = ROTR(x, 8)
                x += y;
                x ^= k;
                y = (y << 3) | (y >> (64 - 3)); // y = ROTL(y, 3)
                y ^= x;
            }

            const uint64_t ROUNDS128128 = 32;

            // Generate key schedule and encrypt at the same time
            public static void encrypt_128_128(Block128 key, ref Block128 ciphertext)
            {
                for (uint64_t i = 0; i < ROUNDS128128; i++) {
                    speck_round_64(ref ciphertext.v1, ref ciphertext.v0, key.v0);
                    speck_round_64(ref key.v1, ref key.v0, i);
                }
            }

            public static uint64_t[] getKeySchedules_128_128(Block128 key)
            {
                var keys = new uint64_t[ROUNDS128128];
                for (uint64_t i = 0; i < ROUNDS128128; i++) {
                    keys[i] = key.v0;
                    speck_round_64(ref key.v1, ref key.v0, i);
                }
                return keys;
            }

            public static void encrypt_128_128(uint64_t[] keySchedules, ref Block128 ciphertext)
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
