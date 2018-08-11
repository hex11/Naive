﻿/* This is .NET safe implementation of Crc32 algorithm.
 * This implementation was investigated as fastest from different variants. It based on Robert Vazan native implementations of Crc32C
 * Also, it is good for x64 and for x86, so, it seems, there is no sense to do 2 different realizations.
 * 
 * Addition: some speed increase was found with splitting xor to 4 independent blocks. Also, some attempts to optimize unaligned tails was unsuccessfull (JIT limitations?).
 * 
 * 
 * Max Vysokikh, 2016-2017
 */

// https://github.com/force-net/Crc32.NET

using System;
using System.Security.Cryptography;

using Naive.HttpSvr;
using Force.Crc32;

namespace NaiveSocks
{
    public class Crc32c
    {

        public static uint Compute(BytesView bv)
        {
            return Append(bv, 0);
        }

        public static uint Append(BytesView bv, uint initial)
        {
            uint crc = initial;
            while (bv != null) {
                if (bv.len > 0) {
                    crc = Crc32CAlgorithm.Append(crc, bv.bytes, bv.offset, bv.len);
                }
                bv = bv.nextNode;
            }
            return crc;
        }

        public static uint Append(byte[] bytes, uint initial)
        {
            return Crc32CAlgorithm.Append(initial, bytes);
        }

        public static void AddToFilterBase(FilterBase fb, uint initial)
        {
            uint rCrc = initial;
            uint wCrc = initial;
            int rSeq = 0;
            int wSeq = 0;
            fb.AddFilters(
                fRead: (bv) => {
                    if (bv.nextNode != null)
                        throw new NotImplementedException();
                    var targetCrc = BitConverter.ToUInt32(bv.bytes, bv.offset + bv.len - 4);
                    bv.len -= 4;
                    uint c = rCrc;
                    c = Append(bv, c);
                    c = Append(BitConverter.GetBytes(rSeq++), c);
                    c = Append(BitConverter.GetBytes(bv.len), c);
                    if (c != targetCrc)
                        throw new Exception("CRC32C checksum failed!");
                    rCrc = c;
                },
                fWrite: (bv) => {
                    uint c = wCrc;
                    c = Append(bv, c);
                    c = Append(BitConverter.GetBytes(wSeq++), c);
                    c = Append(BitConverter.GetBytes(bv.tlen), c);
                    wCrc = c;
                    bv.lastNode.nextNode = new BytesView(BitConverter.GetBytes(c));
                });
        }
    }
}

namespace Force.Crc32
{

    /// <summary>
    /// Implementation of CRC-32C (Castagnoli).
    /// This class supports several convenient static methods returning the CRC as UInt32.
    /// </summary>
    public class Crc32CAlgorithm : HashAlgorithm
    {
        private uint _currentCrc;

        private readonly bool _isBigEndian = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="Crc32CAlgorithm"/> class. 
        /// </summary>
        public Crc32CAlgorithm()
        {
#if !NETCORE13
            HashSizeValue = 32;
#endif
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Crc32CAlgorithm"/> class. 
        /// </summary>
        /// <param name="isBigEndian">Should return bytes result as big endian or little endian</param>
        public Crc32CAlgorithm(bool isBigEndian = true)
            : this()
        {
            _isBigEndian = isBigEndian;
        }

        /// <summary>
        /// Computes CRC-32C from multiple buffers.
        /// Call this method multiple times to chain multiple buffers.
        /// </summary>
        /// <param name="initial">
        /// Initial CRC value for the algorithm. It is zero for the first buffer.
        /// Subsequent buffers should have their initial value set to CRC value returned by previous call to this method.
        /// </param>
        /// <param name="input">Input buffer with data to be checksummed.</param>
        /// <param name="offset">Offset of the input data within the buffer.</param>
        /// <param name="length">Length of the input data in the buffer.</param>
        /// <returns>Accumulated CRC-32C of all buffers processed so far.</returns>
        public static uint Append(uint initial, byte[] input, int offset, int length)
        {
            if (input == null)
                throw new ArgumentNullException("input");
            if (offset < 0 || length < 0 || offset + length > input.Length)
                throw new ArgumentOutOfRangeException("length");
            return AppendInternal(initial, input, offset, length);
        }

        /// <summary>
        /// Computes CRC-32C from multiple buffers.
        /// Call this method multiple times to chain multiple buffers.
        /// </summary>
        /// <param name="initial">
        /// Initial CRC value for the algorithm. It is zero for the first buffer.
        /// Subsequent buffers should have their initial value set to CRC value returned by previous call to this method.
        /// </param>
        /// <param name="input">Input buffer containing data to be checksummed.</param>
        /// <returns>Accumulated CRC-32C of all buffers processed so far.</returns>
        public static uint Append(uint initial, byte[] input)
        {
            if (input == null)
                throw new ArgumentNullException("input");
            return AppendInternal(initial, input, 0, input.Length);
        }

        /// <summary>
        /// Computes CRC-32C from input buffer.
        /// </summary>
        /// <param name="input">Input buffer with data to be checksummed.</param>
        /// <param name="offset">Offset of the input data within the buffer.</param>
        /// <param name="length">Length of the input data in the buffer.</param>
        /// <returns>CRC-32C of the data in the buffer.</returns>
        public static uint Compute(byte[] input, int offset, int length)
        {
            return Append(0, input, offset, length);
        }

        /// <summary>
        /// Computes CRC-32C from input buffer.
        /// </summary>
        /// <param name="input">Input buffer containing data to be checksummed.</param>
        /// <returns>CRC-32C of the buffer.</returns>
        public static uint Compute(byte[] input)
        {
            return Append(0, input);
        }

        /// <summary>
        /// Computes CRC-32C from input buffer and writes it after end of data (buffer should have 4 bytes reserved space for it). Can be used in conjunction with <see cref="IsValidWithCrcAtEnd(byte[],int,int)"/>
        /// </summary>
        /// <param name="input">Input buffer with data to be checksummed.</param>
        /// <param name="offset">Offset of the input data within the buffer.</param>
        /// <param name="length">Length of the input data in the buffer.</param>
        /// <returns>CRC-32C of the data in the buffer.</returns>
        public static uint ComputeAndWriteToEnd(byte[] input, int offset, int length)
        {
            if (length + 4 > input.Length)
                throw new ArgumentOutOfRangeException("length", "Length of data should be less than array length - 4 bytes of CRC data");
            var crc = Append(0, input, offset, length);
            var r = offset + length;
            input[r] = (byte)crc;
            input[r + 1] = (byte)(crc >> 8);
            input[r + 2] = (byte)(crc >> 16);
            input[r + 3] = (byte)(crc >> 24);
            return crc;
        }

        /// <summary>
        /// Computes CRC-32C from input buffer - 4 bytes and writes it as last 4 bytes of buffer. Can be used in conjunction with <see cref="IsValidWithCrcAtEnd(byte[])"/>
        /// </summary>
        /// <param name="input">Input buffer with data to be checksummed.</param>
        /// <returns>CRC-32C of the data in the buffer.</returns>
        public static uint ComputeAndWriteToEnd(byte[] input)
        {
            if (input.Length < 4)
                throw new ArgumentOutOfRangeException("input", "Input array should be 4 bytes at least");
            return ComputeAndWriteToEnd(input, 0, input.Length - 4);
        }

        /// <summary>
        /// Validates correctness of CRC-32C data in source buffer with assumption that CRC-32C data located at end of buffer in reverse bytes order. Can be used in conjunction with <see cref="ComputeAndWriteToEnd(byte[],int,int)"/>
        /// </summary>
        /// <param name="input">Input buffer with data to be checksummed.</param>
        /// <param name="offset">Offset of the input data within the buffer.</param>
        /// <param name="lengthWithCrc">Length of the input data in the buffer with CRC-32C bytes.</param>
        /// <returns>Is checksum valid.</returns>
        public static bool IsValidWithCrcAtEnd(byte[] input, int offset, int lengthWithCrc)
        {
            return Append(0, input, offset, lengthWithCrc) == 0x48674BC7;
        }

        /// <summary>
        /// Validates correctness of CRC-32C data in source buffer with assumption that CRC-32C data located at end of buffer in reverse bytes order. Can be used in conjunction with <see cref="ComputeAndWriteToEnd(byte[],int,int)"/>
        /// </summary>
        /// <param name="input">Input buffer with data to be checksummed.</param>
        /// <returns>Is checksum valid.</returns>
        public static bool IsValidWithCrcAtEnd(byte[] input)
        {
            if (input.Length < 4)
                throw new ArgumentOutOfRangeException("input", "Input array should be 4 bytes at least");
            return Append(0, input, 0, input.Length) == 0x48674BC7;
        }

        /// <summary>
        /// Resets internal state of the algorithm. Used internally.
        /// </summary>
        public override void Initialize()
        {
            _currentCrc = 0;
        }

        /// <summary>
        /// Appends CRC-32C from given buffer
        /// </summary>
        protected override void HashCore(byte[] input, int offset, int length)
        {
            _currentCrc = AppendInternal(_currentCrc, input, offset, length);
        }

        /// <summary>
        /// Computes CRC-32C from <see cref="HashCore"/>
        /// </summary>
        protected override byte[] HashFinal()
        {
            if (_isBigEndian)
                return new[] { (byte)(_currentCrc >> 24), (byte)(_currentCrc >> 16), (byte)(_currentCrc >> 8), (byte)_currentCrc };
            else
                return new[] { (byte)_currentCrc, (byte)(_currentCrc >> 8), (byte)(_currentCrc >> 16), (byte)(_currentCrc >> 24) };
        }

        private static readonly SafeProxyC _proxy = new SafeProxyC();

        private static uint AppendInternal(uint initial, byte[] input, int offset, int length)
        {
            if (length > 0) {
                return _proxy.Append(initial, input, offset, length);
            } else
                return initial;
        }
    }

    internal class SafeProxy
    {
        private const uint Poly = 0xedb88320u;

        private readonly uint[] _table = new uint[16 * 256];

        internal SafeProxy()
        {
            Init(Poly);
        }

        protected void Init(uint poly)
        {
            var table = _table;
            for (uint i = 0; i < 256; i++) {
                uint res = i;
                for (int t = 0; t < 16; t++) {
                    for (int k = 0; k < 8; k++) res = (res & 1) == 1 ? poly ^ (res >> 1) : (res >> 1);
                    table[(t * 256) + i] = res;
                }
            }
        }

        public uint Append(uint crc, byte[] input, int offset, int length)
        {
            uint crcLocal = uint.MaxValue ^ crc;

            uint[] table = _table;
            while (length >= 16) {
                var a = table[(3 * 256) + input[offset + 12]]
                    ^ table[(2 * 256) + input[offset + 13]]
                    ^ table[(1 * 256) + input[offset + 14]]
                    ^ table[(0 * 256) + input[offset + 15]];

                var b = table[(7 * 256) + input[offset + 8]]
                    ^ table[(6 * 256) + input[offset + 9]]
                    ^ table[(5 * 256) + input[offset + 10]]
                    ^ table[(4 * 256) + input[offset + 11]];

                var c = table[(11 * 256) + input[offset + 4]]
                    ^ table[(10 * 256) + input[offset + 5]]
                    ^ table[(9 * 256) + input[offset + 6]]
                    ^ table[(8 * 256) + input[offset + 7]];

                var d = table[(15 * 256) + ((byte)crcLocal ^ input[offset])]
                    ^ table[(14 * 256) + ((byte)(crcLocal >> 8) ^ input[offset + 1])]
                    ^ table[(13 * 256) + ((byte)(crcLocal >> 16) ^ input[offset + 2])]
                    ^ table[(12 * 256) + ((crcLocal >> 24) ^ input[offset + 3])];

                crcLocal = d ^ c ^ b ^ a;
                offset += 16;
                length -= 16;
            }

            while (--length >= 0)
                crcLocal = table[(byte)(crcLocal ^ input[offset++])] ^ crcLocal >> 8;

            return crcLocal ^ uint.MaxValue;
        }
    }

    internal class SafeProxyC : SafeProxy
    {
        private const uint Poly = 0x82F63B78u;

        internal SafeProxyC()
        {
            Init(Poly);
        }
    }
}