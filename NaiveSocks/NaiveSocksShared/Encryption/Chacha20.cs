// chacha20-ietf implementation
// Based on https://github.com/sbennett1990/ChaCha20-csharp/blob/master/ChaCha20Cipher.cs
// 

/*
 * Copyright (c) 2015 Scott Bennett, 2017 - 2020 Hex Eleven
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
            KeySetup(key);
        }

        public static ChaCha20IetfEncryptor Create(byte[] key) => new ChaCha20IetfEncryptor(key);

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
            int keyIndex = key.Length - 16;
            fixed (uint* state = ctx.state)
            fixed (byte* k = key) {
                state[0] = Chacha.Constants[0];
                state[1] = Chacha.Constants[1];
                state[2] = Chacha.Constants[2];
                state[3] = Chacha.Constants[3];

                state[4] = Chacha.U8To32Little(k, 0);
                state[5] = Chacha.U8To32Little(k, 4);
                state[6] = Chacha.U8To32Little(k, 8);
                state[7] = Chacha.U8To32Little(k, 12);

                state[8] = Chacha.U8To32Little(k, 16);
                state[9] = Chacha.U8To32Little(k, 20);
                state[10] = Chacha.U8To32Little(k, 24);
                state[11] = Chacha.U8To32Little(k, 28);
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
            fixed (byte* n = nonce) {
                ctx.state[12] = counter;
                ctx.state[13] = Chacha.U8To32Little(n, 0);
                ctx.state[14] = Chacha.U8To32Little(n, 4);
                ctx.state[15] = Chacha.U8To32Little(n, 8);
            }
        }

        public uint Counter
        {
            get {
                return ctx.state[12];
            }
            set {
                ctx.state[12] = value;
            }
        }

        public override int IVLength => 12;

        Context ctx;

        private unsafe struct Context
        {
            public fixed uint state[16];
            public fixed byte keyStreamBuffer[64];
        }

        const int KeystreamBufferSize = 64;
        uint keystreamBufferPos = KeystreamBufferSize;

        protected override void IVSetup(byte[] IV)
        {
            IVSetup(IV, 0);
        }

        public override void Update(BytesSegment bs)
        {
            bs.CheckAsParameter();
            var pos = (uint)bs.Offset;
            var end = pos + (uint)bs.Len;
            fixed (Context* ctx = &this.ctx)
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
                    NaiveUtils.XorBytesUnsafe(ctx->keyStreamBuffer + keystreamBufferPos, bytes + pos, count);
                    pos += count;
                    keystreamBufferPos += count;
                }
            }
        }

        private static void NextKeystreamBuffer(Context* ctx)
        {
            Chacha.ChachaBlockFunction(ctx->state, (uint*)ctx->keyStreamBuffer, 20 / 2);
            ctx->state[12]++;
        }
    }

    public static unsafe class Chacha
    {
        public static readonly uint[] Constants = new uint[] {
            0x61707865, 0x3320646e, 0x79622d32, 0x6b206574
        };

        /// <summary>
        /// Compute HChaCha subkey
        /// </summary>
        /// <param name="key">256-bit key input</param>
        /// <param name="nonce">128-bit nonce input</param>
        /// <param name="dst">256-bit output</param>
        /// <param name="iterations">ChaCha block function iterations. It's 10 for HChaCha20.</param>
        /// <see cref="https://tools.ietf.org/html/draft-arciszewski-xchacha-03#section-2.2"/>"/>
        public static void HChacha(byte* key, byte* nonce, byte* dst, int iterations)
        {
            // Prepare for input of ChaCha block function
            var state = stackalloc uint[16];
            for (int i = 0; i < 4; i++)
                state[i] = Constants[i];
            for (int i = 0; i < 8; i++)
                state[4 + i] = U8To32Little(key, i * 4);
            for (int i = 0; i < 4; i++)
                state[24 + i] = U8To32Little(nonce, i * 4);

            // Perform ChaCha block iterations
            ChachaBlockFunction(state, state, iterations);

            // Store resultant HChaCha subkey
            for (int i = 0; i < 4; i++)
                U8From32Little(state[i], dst, i * 4);
            for (int i = 0; i < 4; i++)
                U8From32Little(state[12 + i], dst, 16 + i * 4);
        }

        /// <summary>
        /// Perform ChaCha iterations on `state` and store the result to `dst`.
        /// Note that `state` and `dst` could be the same address.
        /// </summary>
        /// <param name="iterations">Should be 10 for ChaCha20</param>
        /// <see cref="https://tools.ietf.org/html/rfc7539#section-2.3"/>
        public static void ChachaBlockFunction(uint* state, uint* dst, int iterations)
        {
            // Load state into local variables (which are stored in CPU registers, hopefully)
            uint x0, x1, x2, x3, x4, x5, x6, x7, x8, x9, xa, xb, xc, xd, xe, xf;
            {
                int i = 0;
                x0 = state[i++];
                x1 = state[i++];
                x2 = state[i++];
                x3 = state[i++];
                x4 = state[i++];
                x5 = state[i++];
                x6 = state[i++];
                x7 = state[i++];
                x8 = state[i++];
                x9 = state[i++];
                xa = state[i++];
                xb = state[i++];
                xc = state[i++];
                xd = state[i++];
                xe = state[i++];
                xf = state[i++];
            }

            // Do ChaCha block iterations
            for (int i = 0; i < iterations; i++)
            {
                // These lines of code are inlined and reordered manually.

                //// a "column round":
                // QUARTERROUND ( 0, 4, 8,12)
                // QUARTERROUND ( 1, 5, 9,13)
                // QUARTERROUND ( 2, 6,10,14)
                // QUARTERROUND ( 3, 7,11,15)
                x0 += x4; xc ^= x0; xc = (xc << 16) | (xc >> (32 - 16));
                x1 += x5; xd ^= x1; xd = (xd << 16) | (xd >> (32 - 16));
                x2 += x6; xe ^= x2; xe = (xe << 16) | (xe >> (32 - 16));
                x3 += x7; xf ^= x3; xf = (xf << 16) | (xf >> (32 - 16));
                x8 += xc; x4 ^= x8; x4 = (x4 << 12) | (x4 >> (32 - 12));
                x9 += xd; x5 ^= x9; x5 = (x5 << 12) | (x5 >> (32 - 12));
                xa += xe; x6 ^= xa; x6 = (x6 << 12) | (x6 >> (32 - 12));
                xb += xf; x7 ^= xb; x7 = (x7 << 12) | (x7 >> (32 - 12));
                x0 += x4; xc ^= x0; xc = (xc << 8) | (xc >> (32 - 8));
                x1 += x5; xd ^= x1; xd = (xd << 8) | (xd >> (32 - 8));
                x2 += x6; xe ^= x2; xe = (xe << 8) | (xe >> (32 - 8));
                x3 += x7; xf ^= x3; xf = (xf << 8) | (xf >> (32 - 8));
                x8 += xc; x4 ^= x8; x4 = (x4 << 7) | (x4 >> (32 - 7));
                x9 += xd; x5 ^= x9; x5 = (x5 << 7) | (x5 >> (32 - 7));
                xa += xe; x6 ^= xa; x6 = (x6 << 7) | (x6 >> (32 - 7));
                xb += xf; x7 ^= xb; x7 = (x7 << 7) | (x7 >> (32 - 7));


                //// a "diagonal round":
                // QUARTERROUND ( 0, 5,10,15)
                // QUARTERROUND ( 1, 6,11,12)
                // QUARTERROUND ( 2, 7, 8,13)
                // QUARTERROUND ( 3, 4, 9,14)
                x0 += x5; xf ^= x0; xf = (xf << 16) | (xf >> (32 - 16));
                x1 += x6; xc ^= x1; xc = (xc << 16) | (xc >> (32 - 16));
                x2 += x7; xd ^= x2; xd = (xd << 16) | (xd >> (32 - 16));
                x3 += x4; xe ^= x3; xe = (xe << 16) | (xe >> (32 - 16));
                xa += xf; x5 ^= xa; x5 = (x5 << 12) | (x5 >> (32 - 12));
                xb += xc; x6 ^= xb; x6 = (x6 << 12) | (x6 >> (32 - 12));
                x8 += xd; x7 ^= x8; x7 = (x7 << 12) | (x7 >> (32 - 12));
                x9 += xe; x4 ^= x9; x4 = (x4 << 12) | (x4 >> (32 - 12));
                x0 += x5; xf ^= x0; xf = (xf << 8) | (xf >> (32 - 8));
                x1 += x6; xc ^= x1; xc = (xc << 8) | (xc >> (32 - 8));
                x2 += x7; xd ^= x2; xd = (xd << 8) | (xd >> (32 - 8));
                x3 += x4; xe ^= x3; xe = (xe << 8) | (xe >> (32 - 8));
                xa += xf; x5 ^= xa; x5 = (x5 << 7) | (x5 >> (32 - 7));
                xb += xc; x6 ^= xb; x6 = (x6 << 7) | (x6 >> (32 - 7));
                x8 += xd; x7 ^= x8; x7 = (x7 << 7) | (x7 >> (32 - 7));
                x9 += xe; x4 ^= x9; x4 = (x4 << 7) | (x4 >> (32 - 7));

            }

            // Store results to dst
            {
                int i = 0;
                dst[i] = x0 + state[i]; i++;
                dst[i] = x1 + state[i]; i++;
                dst[i] = x2 + state[i]; i++;
                dst[i] = x3 + state[i]; i++;
                dst[i] = x4 + state[i]; i++;
                dst[i] = x5 + state[i]; i++;
                dst[i] = x6 + state[i]; i++;
                dst[i] = x7 + state[i]; i++;
                dst[i] = x8 + state[i]; i++;
                dst[i] = x9 + state[i]; i++;
                dst[i] = xa + state[i]; i++;
                dst[i] = xb + state[i]; i++;
                dst[i] = xc + state[i]; i++;
                dst[i] = xd + state[i]; i++;
                dst[i] = xe + state[i]; i++;
                dst[i] = xf + state[i];
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void U8From32Little(uint u32, byte* p, int outputOffset)
        {
            p[outputOffset] = (byte)u32;
            p[outputOffset + 1] = (byte)(u32 >> 8);
            p[outputOffset + 2] = (byte)(u32 >> 16);
            p[outputOffset + 3] = (byte)(u32 >> 24);
        }
    }
}