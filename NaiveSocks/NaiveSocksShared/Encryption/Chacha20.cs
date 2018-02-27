// chacha20-ietf implementation
// https://github.com/sbennett1990/ChaCha20-csharp/blob/master/ChaCha20Cipher.cs
// (modified)

/*
 * Copyright (c) 2015 Scott Bennett, 2017 - 2018 Hex Eleven
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 * * Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * 
 * * Redistributions in binary form must reproduce the above copyright notice,
 *   this list of conditions and the following disclaimer in the documentation
 *   and/or other materials provided with the distribution.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Naive.HttpSvr;

namespace NaiveSocks
{
    public sealed unsafe class ChaCha20IetfEncryptor : IVEncryptorBase
    {
        /// <summary>
        /// Set up a new ChaCha20 state. The lengths of the given parameters are 
        /// checked before encryption happens. 
        /// </summary>
        /// <remarks>
        /// See <a href="https://tools.ietf.org/html/rfc7539#page-10">ChaCha20 Spec Section 2.4</a>
        /// for a detailed description of the inputs. 
        /// </remarks>
        /// <param name="key">
        /// A 32-byte (256-bit) key, treated as a concatenation of eight 32-bit 
        /// little-endian integers
        /// </param>
        /// <param name="nonce">
        /// A 12-byte (96-bit) nonce, treated as a concatenation of three 32-bit 
        /// little-endian integers
        /// </param>
        /// <param name="counter">
        /// A 4-byte (32-bit) block counter, treated as a 32-bit little-endian integer
        /// </param>
        public ChaCha20IetfEncryptor(byte[] key/*, byte[] nonce, uint counter*/)
        {
            ctx = (Context*)Marshal.AllocHGlobal(sizeof(Context));
            KeySetup(key);
        }

        ~ChaCha20IetfEncryptor()
        {
            Marshal.FreeHGlobal((IntPtr)ctx);
        }

        public static ChaCha20IetfEncryptor Create(byte[] key) => new ChaCha20IetfEncryptor(key);

        static readonly byte[] sigma = Encoding.ASCII.GetBytes("expand 32-byte k");
        static readonly byte[] tau = Encoding.ASCII.GetBytes("expand 16-byte k");

        /// <summary>
        /// Set up the ChaCha state with the given key. A 32-byte key is required 
        /// and enforced. 
        /// </summary>
        /// <param name="key">
        /// A 32-byte (256-bit) key, treated as a concatenation of eight 32-bit 
        /// little-endian integers
        /// </param>
        private void KeySetup(byte[] key)
        {
            if (key == null) {
                throw new ArgumentNullException("Key is null");
            }
            if (key.Length != 32) {
                throw new ArgumentException("Key length must be 32. Actual is " + key.Length.ToString());
            }

            // These are the same constants defined in the reference implementation
            // see http://cr.yp.to/streamciphers/timings/estreambench/submissions/salsa20/chacha8/ref/chacha.c
            byte[] constants = (key.Length == 32) ? sigma : tau;
            int keyIndex = key.Length - 16;
            uint* state = ctx->state;
            fixed (byte* c = constants)
            fixed (byte* k = key) {
                state[4] = U8To32Little(k, 0);
                state[5] = U8To32Little(k, 4);
                state[6] = U8To32Little(k, 8);
                state[7] = U8To32Little(k, 12);

                state[8] = U8To32Little(k, keyIndex + 0);
                state[9] = U8To32Little(k, keyIndex + 4);
                state[10] = U8To32Little(k, keyIndex + 8);
                state[11] = U8To32Little(k, keyIndex + 12);

                state[0] = U8To32Little(c, 0);
                state[1] = U8To32Little(c, 4);
                state[2] = U8To32Little(c, 8);
                state[3] = U8To32Little(c, 12);
            }
        }

        /// <summary>
        /// Set up the ChaCha state with the given nonce (aka Initialization Vector 
        /// or IV) and block counter. A 12-byte nonce and a 4-byte counter are 
        /// required and enforced. 
        /// </summary>
        /// <param name="nonce">
        /// A 12-byte (96-bit) nonce, treated as a concatenation of three 32-bit 
        /// little-endian integers
        /// </param>
        /// <param name="counter">
        /// A 4-byte (32-bit) block counter, treated as a 32-bit little-endian integer
        /// </param>
        public void IVSetup(byte[] nonce, uint counter)
        {
            if (nonce == null) {
                throw new ArgumentNullException("Nonce is null");
            }
            if (nonce.Length != 12) {
                throw new ArgumentException(
                    "Nonce length should be 12. Actual is " + nonce.Length.ToString()
                );
            }
            _iv = nonce;
            uint* state = ctx->state;
            fixed (byte* n = nonce) {
                state[12] = counter;
                state[13] = U8To32Little(n, 0);
                state[14] = U8To32Little(n, 4);
                state[15] = U8To32Little(n, 8);
            }
        }

        public uint Counter
        {
            get {
                return ctx->state[12];
            }
            set {
                ctx->state[12] = value;
            }
        }

        public override int IVLength => 12;

        Context* ctx;

        private unsafe struct Context
        {
            public fixed uint state[16];
            public fixed byte keyStreamBuffer[64];
        }

        const int KeystreamBufferSize = 64;
        int keystreamBufferPos = KeystreamBufferSize;

        protected override void IVSetup(byte[] IV)
        {
            IVSetup(IV, 0);
        }

        public override void Update(BytesSegment bs)
        {
            bs.CheckAsParameter();
            var pos = bs.Offset;
            var end = pos + bs.Len;
            var ksb = ctx->keyStreamBuffer;
            fixed (byte* bytes = bs.Bytes) {
                while (pos < end) {
                    var remainningKeystream = KeystreamBufferSize - keystreamBufferPos;
                    if (remainningKeystream == 0) {
                        keystreamBufferPos = 0;
                        remainningKeystream = KeystreamBufferSize;
                        NextKeystreamBuffer(ctx);
                    }
                    var count = end - pos;
                    count = count < remainningKeystream ? count : remainningKeystream;
                    NaiveUtils.XorBytesUnsafe(ksb + keystreamBufferPos, bytes + pos, count);
                    pos += count;
                    keystreamBufferPos += count;
                }
            }
        }

        private static void NextKeystreamBuffer(Context* ctx)
        {
            var x = stackalloc uint[16];
            var state = ctx->state;
            var keyStreamBuffer = ctx->keyStreamBuffer;
            for (int i = 0; i < 16; i++) {
                x[i] = state[i];
            }
            for (int i = 0; i < 10; i++) {
                // inlined:
                //QuarterRound(x, 0, 4, 8, 12);
                //QuarterRound(x, 1, 5, 9, 13);
                //QuarterRound(x, 2, 6, 10, 14);
                //QuarterRound(x, 3, 7, 11, 15);

                //QuarterRound(x, 0, 5, 10, 15);
                //QuarterRound(x, 1, 6, 11, 12);
                //QuarterRound(x, 2, 7, 8, 13);
                //QuarterRound(x, 3, 4, 9, 14);

                x[0] += x[4]; x[12] = ((x[12] ^ x[0]) << 16) | ((x[12] ^ x[0]) >> (32 - 16));
                x[8] += x[12]; x[4] = ((x[4] ^ x[8]) << 12) | ((x[4] ^ x[8]) >> (32 - 12));
                x[0] += x[4]; x[12] = ((x[12] ^ x[0]) << 8) | ((x[12] ^ x[0]) >> (32 - 8));
                x[8] += x[12]; x[4] = ((x[4] ^ x[8]) << 7) | ((x[4] ^ x[8]) >> (32 - 7));

                x[1] += x[5]; x[13] = ((x[13] ^ x[1]) << 16) | ((x[13] ^ x[1]) >> (32 - 16));
                x[9] += x[13]; x[5] = ((x[5] ^ x[9]) << 12) | ((x[5] ^ x[9]) >> (32 - 12));
                x[1] += x[5]; x[13] = ((x[13] ^ x[1]) << 8) | ((x[13] ^ x[1]) >> (32 - 8));
                x[9] += x[13]; x[5] = ((x[5] ^ x[9]) << 7) | ((x[5] ^ x[9]) >> (32 - 7));

                x[2] += x[6]; x[14] = ((x[14] ^ x[2]) << 16) | ((x[14] ^ x[2]) >> (32 - 16));
                x[10] += x[14]; x[6] = ((x[6] ^ x[10]) << 12) | ((x[6] ^ x[10]) >> (32 - 12));
                x[2] += x[6]; x[14] = ((x[14] ^ x[2]) << 8) | ((x[14] ^ x[2]) >> (32 - 8));
                x[10] += x[14]; x[6] = ((x[6] ^ x[10]) << 7) | ((x[6] ^ x[10]) >> (32 - 7));

                x[3] += x[7]; x[15] = ((x[15] ^ x[3]) << 16) | ((x[15] ^ x[3]) >> (32 - 16));
                x[11] += x[15]; x[7] = ((x[7] ^ x[11]) << 12) | ((x[7] ^ x[11]) >> (32 - 12));
                x[3] += x[7]; x[15] = ((x[15] ^ x[3]) << 8) | ((x[15] ^ x[3]) >> (32 - 8));
                x[11] += x[15]; x[7] = ((x[7] ^ x[11]) << 7) | ((x[7] ^ x[11]) >> (32 - 7));

                /////////////////

                x[0] += x[5]; x[15] = ((x[15] ^ x[0]) << 16) | ((x[15] ^ x[0]) >> (32 - 16));
                x[10] += x[15]; x[5] = ((x[5] ^ x[10]) << 12) | ((x[5] ^ x[10]) >> (32 - 12));
                x[0] += x[5]; x[15] = ((x[15] ^ x[0]) << 8) | ((x[15] ^ x[0]) >> (32 - 8));
                x[10] += x[15]; x[5] = ((x[5] ^ x[10]) << 7) | ((x[5] ^ x[10]) >> (32 - 7));

                x[1] += x[6]; x[12] = ((x[12] ^ x[1]) << 16) | ((x[12] ^ x[1]) >> (32 - 16));
                x[11] += x[12]; x[6] = ((x[6] ^ x[11]) << 12) | ((x[6] ^ x[11]) >> (32 - 12));
                x[1] += x[6]; x[12] = ((x[12] ^ x[1]) << 8) | ((x[12] ^ x[1]) >> (32 - 8));
                x[11] += x[12]; x[6] = ((x[6] ^ x[11]) << 7) | ((x[6] ^ x[11]) >> (32 - 7));

                x[2] += x[7]; x[13] = ((x[13] ^ x[2]) << 16) | ((x[13] ^ x[2]) >> (32 - 16));
                x[8] += x[13]; x[7] = ((x[7] ^ x[8]) << 12) | ((x[7] ^ x[8]) >> (32 - 12));
                x[2] += x[7]; x[13] = ((x[13] ^ x[2]) << 8) | ((x[13] ^ x[2]) >> (32 - 8));
                x[8] += x[13]; x[7] = ((x[7] ^ x[8]) << 7) | ((x[7] ^ x[8]) >> (32 - 7));

                x[3] += x[4]; x[14] = ((x[14] ^ x[3]) << 16) | ((x[14] ^ x[3]) >> (32 - 16));
                x[9] += x[14]; x[4] = ((x[4] ^ x[9]) << 12) | ((x[4] ^ x[9]) >> (32 - 12));
                x[3] += x[4]; x[14] = ((x[14] ^ x[3]) << 8) | ((x[14] ^ x[3]) >> (32 - 8));
                x[9] += x[14]; x[4] = ((x[4] ^ x[9]) << 7) | ((x[4] ^ x[9]) >> (32 - 7));
            }
            uint* ksAsUint = (uint*)keyStreamBuffer;
            for (int i = 0; i < 16; i++) {
                ksAsUint[i] = x[i] + state[i];
            }
            if (++state[12] == 0) {
                /* Stopping at 2^70 bytes per nonce is the user's responsibility */
                state[13]++;
            }
        }

        /// <summary>
        /// Convert four bytes of the input buffer into an unsigned 
        /// 32-bit integer, beginning at the inputOffset. 
        /// </summary>
        /// <param name="p"></param>
        /// <param name="inputOffset"></param>
        /// <returns>An unsigned 32-bit integer</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint U8To32Little(byte* p, int inputOffset)
        {
            return ((uint)p[inputOffset] |
                   ((uint)p[inputOffset + 1] << 8) |
                   ((uint)p[inputOffset + 2] << 16) |
                   ((uint)p[inputOffset + 3] << 24));
        }
    }
}