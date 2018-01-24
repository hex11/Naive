using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Naive.HttpSvr
{
    public class BytesView
    {
        public byte[] bytes;
        public int offset;
        public int len;
        public BytesView nextNode;

        public BytesView()
        {
        }

        public BytesView(byte[] bytes)
        {
            Set(bytes);
        }

        public BytesView(byte[] bytes, int offset, int len)
        {
            Set(bytes, offset, len);
        }

        public void Set(byte[] bytes)
        {
            this.bytes = bytes;
            this.offset = 0;
            this.len = bytes.Length;
        }

        public void Set(byte[] bytes, int offset, int len)
        {
            this.bytes = bytes;
            this.offset = offset;
            this.len = len;
        }

        public void Set(BytesView bv)
        {
            this.bytes = bv.bytes;
            this.offset = bv.offset;
            this.len = bv.len;
        }

        public BytesView Clone()
        {
            return new BytesView(bytes, offset, len) { nextNode = nextNode };
        }

        public void Sub(int startIndex)
        {
            // TODO
            if (len < startIndex)
                throw new NotImplementedException();
            offset += startIndex;
            len -= startIndex;
        }

        public byte[] GetBytes() => GetBytes(0, tlen);
        public byte[] GetBytes(bool forceNew) => GetBytes(0, tlen, forceNew);
        public byte[] GetBytes(int offset, int len) => GetBytes(offset, len, false);
        public byte[] GetBytes(int offset, int len, bool forceNew)
        {
            if (!forceNew && offset == 0 & this.offset == 0 & len == bytes.Length) {
                return bytes;
            }
            var buf = new Byte[len];
            for (int i = 0; i < len; i++) {
                buf[i] = this[offset + i];
            }
            return buf;
        }

        public BytesView lastNode
        {
            get {
                var curnode = this;
                while (curnode.nextNode != null) {
                    curnode = curnode.nextNode;
                }
                return curnode;
            }
        }

        public int tlen
        {
            get {
                var len = 0;
                var curnode = this;
                do {
                    len += curnode.len;
                } while ((curnode = curnode.nextNode) != null);
                return len;
            }
        }

        public byte this[int index]
        {
            get {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                var pos = 0;
                var curnode = this;
                do {
                    if (index < pos + curnode.len) {
                        return curnode.bytes[curnode.offset + index - pos];
                    }
                    pos += curnode.len;
                } while ((curnode = curnode.nextNode) != null);
                throw new IndexOutOfRangeException();
            }
            set {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                var pos = 0;
                var curnode = this;
                do {
                    if (index < pos + curnode.len) {
                        curnode.bytes[curnode.offset + index - pos] = value;
                        return;
                    }
                    pos += curnode.len;
                } while ((curnode = curnode.nextNode) != null);
            }
        }

        public override string ToString()
        {
            int n = 1;
            var node = this;
            while ((node = node.nextNode) != null) {
                n++;
            }
            StringBuilder sb = new StringBuilder($"{{BytesView n={n} tlen={tlen}| ");
            var tooLong = tlen > 12;
            var shownSize = Math.Min(12, tlen);
            for (int i = 0; i < shownSize; i++) {
                sb.Append(this[i]);
                sb.Append(',');
            }
            sb.Remove(sb.Length - 1, 1);
            if (tooLong)
                sb.Append("...");
            sb.Append('}');
            return sb.ToString();
        }

        public static implicit operator BytesView(byte[] bytes)
        {
            return new BytesView(bytes);
        }

        public BufferEnumerator GetEnumerator()
        {
            return new BufferEnumerator(this);
        }

        public struct BufferEnumerator : IEnumerator<BytesView>
        {
            public BufferEnumerator(BytesView firstNode)
            {
                FirstNode = firstNode;
                Current = null;
            }

            BytesView FirstNode { get; }
            public BytesView Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (Current == null) {
                    Current = FirstNode;
                    return true;
                }
                Current = Current.nextNode;
                return Current != null;
            }

            public void Reset()
            {
                Current = null;
            }
        }
    }
}
