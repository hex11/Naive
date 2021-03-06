﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Naive.HttpSvr
{
    public struct BytesSegment : IEnumerable<byte> // implemented the interface for convenience in debugging
    {
        public byte[] Bytes;
        public int Offset;
        public int Len;

        public BytesSegment(byte[] bytes)
        {
            this.Bytes = bytes;
            this.Offset = 0;
            this.Len = bytes.Length;
        }

        public BytesSegment(BytesView bv)
        {
            this.Bytes = bv.bytes;
            this.Offset = bv.offset;
            this.Len = bv.len;
        }

        public BytesSegment(byte[] bytes, int offset, int len)
        {
            this.Bytes = bytes;
            this.Offset = offset;
            this.Len = len;
        }

        public void Set(byte[] bytes)
        {
            this.Bytes = bytes;
            this.Offset = 0;
            this.Len = bytes.Length;
        }

        public void Set(byte[] bytes, int offset, int len)
        {
            this.Bytes = bytes;
            this.Offset = offset;
            this.Len = len;
        }

        public byte[] GetBytes()
        {
            return GetBytes(false);
        }

        public byte[] GetBytes(bool forceCreateNew)
        {
            if (!forceCreateNew && (Offset == 0 & Len == Bytes.Length)) {
                return Bytes;
            }
            var buf = new Byte[Len];
            Buffer.BlockCopy(Bytes, Offset, buf, 0, Len);
            return buf;
        }

        public BytesView ToBytesView() => new BytesView(Bytes, Offset, Len);

        public byte this[int index]
        {
            get => Bytes[Offset + index];
            set => Bytes[Offset + index] = value;
        }

        public byte GetChecking(int index)
        {
            if (index < 0 || index >= Len)
                throw new IndexOutOfRangeException();
            return this[index];
        }

        public byte GetOrZero(int index)
        {
            if (index < 0 || index >= Len)
                return 0;
            return this[index];
        }

        public void SubSelf(int begin)
        {
            this.Offset += begin;
            this.Len -= begin;
        }

        public void ResetSelf()
        {
            this.Bytes = null;
            this.Offset = 0;
            this.Len = 0;
        }

        public BytesSegment Sub(int begin) => Sub(begin, Len - begin);
        public BytesSegment Sub(int begin, int count)
        {
            return new BytesSegment(Bytes, Offset + begin, count);
        }

        public void CopyTo(BytesSegment dst) => CopyTo(dst, Len);

        public void CopyTo(BytesSegment dst, int count)
        {
            Copy(Bytes, Offset, dst.Bytes, dst.Offset, count);
        }

        public void CopyTo(BytesSegment dst, int srcBegin, int count)
        {
            Copy(Bytes, Offset + srcBegin, dst.Bytes, dst.Offset, count);
        }

        public void CopyTo(BytesSegment dst, int srcBegin, int count, int dstBegin)
        {
            Copy(Bytes, Offset + srcBegin, dst.Bytes, dst.Offset + dstBegin, count);
        }

        static void Copy(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count)
        {
            if (count > 4096) {
                Buffer.BlockCopy(src, srcOffset, dst, dstOffset, count);
            } else if (count > 128) {
                Array.Copy(src, srcOffset, dst, dstOffset, count);
            } else {
                for (int i = 0; i < count; i++) {
                    dst[dstOffset++] = src[srcOffset++];
                }
            }
        }

        public static implicit operator BytesSegment(byte[] bytes)
        {
            return new BytesSegment(bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CheckAsParameter()
        {
            if (Bytes == null)
                throw new ArgumentNullException("Bytes");
            if (Offset < 0 | Len < 0)
                throw new ArgumentOutOfRangeException("Offset and Len cannot be less than zero");
            if (Bytes.Length < Offset + Len)
                throw new ArgumentException("Bytes.Length < Offset + Len");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CheckAsParameter_AllowNull()
        {
            if (Bytes == null) {
                if (Offset != 0 | Len != 0)
                    throw new ArgumentOutOfRangeException("Both Offset and Len should be 0 when Bytes is null");
            } else {
                if (Offset < 0 | Len < 0)
                    throw new ArgumentOutOfRangeException("Offset and Len cannot be less than zero");
                if (Bytes.Length < Offset + Len)
                    throw new ArgumentException("Bytes.Length < Offset + Len");
            }
        }

        public IEnumerator<byte> GetEnumerator()
        {
            return Bytes.Skip(Offset).Take(Len).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    public static class BytesSegmentExt
    {
        public static void Write(this Stream stream, BytesSegment bs)
        {
            stream.Write(bs.Bytes, bs.Offset, bs.Len);
        }

        public static Task WriteAsync(this Stream stream, BytesSegment bs)
        {
            return stream.WriteAsync(bs.Bytes, bs.Offset, bs.Len);
        }

        public static Task WriteAsync(this Stream stream, BytesSegment bs, CancellationToken cancellationToken)
        {
            return stream.WriteAsync(bs.Bytes, bs.Offset, bs.Len, cancellationToken);
        }

        public static int Read(this Stream stream, BytesSegment bs)
        {
            return stream.Read(bs.Bytes, bs.Offset, bs.Len);
        }

        public static Task<int> ReadAsync(this Stream stream, BytesSegment bs)
        {
            return stream.ReadAsync(bs.Bytes, bs.Offset, bs.Len);
        }

        public static Task<int> ReadAsync(this Stream stream, BytesSegment bs, CancellationToken cancellationToken)
        {
            return stream.ReadAsync(bs.Bytes, bs.Offset, bs.Len, cancellationToken);
        }
    }
}
