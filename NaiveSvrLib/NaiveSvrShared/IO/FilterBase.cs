using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace Naive.HttpSvr
{
    public interface IIVEncryptor
    {
        void Update(BytesSegment bs);
        byte[] IV { get; set; }
        int IVLength { get; }
    }

    public class FilterBase
    {
        public Action<BytesView> ReadFilter;
        public Action<BytesView> WriteFilter;

        public void OnRead(BytesView bv)
        {
            if (bv.tlen > 0)
                ReadFilter?.Invoke(bv);
        }

        public void OnWrite(BytesView bv)
        {
            if (bv.tlen > 0)
                WriteFilter?.Invoke(bv);
        }

        public void ClearFilter()
        {
            WriteFilter = null;
            ReadFilter = null;
        }

        public void AddWriteFilter(Action<BytesView> f)
        {
            WriteFilter = CombineFilter(WriteFilter, f);
        }

        public void AddReadFilter(Action<BytesView> f)
        {
            ReadFilter = CombineFilter(f, ReadFilter);
        }

        public void AddFilters(Action<BytesView> fRead, Action<BytesView> fWrite)
        {
            AddReadFilter(fRead);
            AddWriteFilter(fWrite);
        }

        public static Action<BytesView> CombineFilter(Action<BytesView> f1, Action<BytesView> f2)
        {
            if (f1 == null)
                return f2;
            if (f2 == null)
                return f1;
            return (bv) => {
                f1(bv);
                f2(bv);
            };
        }

        [Obsolete]
        public void ApplyXORFilter(byte[] key)
        {
            var filter = GetXORFilter(key);
            AddWriteFilter(filter);
            AddReadFilter(filter);
        }

        [Obsolete]
        public void ApplyXORFilter2(byte[] key)
        {
            AddWriteFilter(GetXORFilter2(key));
            AddReadFilter(GetXORFilter2(key));
        }

        [Obsolete]
        public void ApplyXORFilter3(byte[] key)
        {
            AddWriteFilter(GetXORFilter3(key));
            AddReadFilter(GetXORFilter3(key));
        }

        [Obsolete]
        public void ApplyAesFilter(byte[] iv, byte[] key)
        {
            AddWriteFilter(GetAesFilter(true, iv, key));
            AddReadFilter(GetAesFilter(false, iv, key));
        }

        public void ApplyAesFilter2(byte[] key)
        {
            AddWriteFilter(GetAesFilter2(true, key));
            AddReadFilter(GetAesFilter2(false, key));
        }

        public void ApplyAesStreamFilter(byte[] key)
        {
            AddWriteFilter(GetAesStreamFilter(true, key));
            AddReadFilter(GetAesStreamFilter(false, key));
        }

        public void ApplyFilterFromEncryptor(byte[] key, Func<bool, byte[], IIVEncryptor> func)
        {
            ApplyFilterFromEncryptor(func(true, key), func(false, key));
        }

        public void ApplyFilterFromEncryptor(byte[] key, Func<byte[], IIVEncryptor> func)
        {
            ApplyFilterFromEncryptor(func(key), func(key));
        }

        public void ApplyFilterFromEncryptor(IIVEncryptor write, IIVEncryptor read)
        {
            AddWriteFilter(GetStreamFilterFromIVEncryptor(true, write));
            AddReadFilter(GetStreamFilterFromIVEncryptor(false, read));
        }

        public void ApplyFilterFromEncryptor<TArg>(Func<IIVEncryptor> creator)
        {
            AddWriteFilter(GetStreamFilterFromIVEncryptor(true, creator()));
            AddReadFilter(GetStreamFilterFromIVEncryptor(false, creator()));
        }

        public void ApplyFilterFromEncryptor<TArg>(Func<TArg, IIVEncryptor> creator, TArg arg)
        {
            AddWriteFilter(GetStreamFilterFromIVEncryptor(true, creator(arg)));
            AddReadFilter(GetStreamFilterFromIVEncryptor(false, creator(arg)));
        }

        public void ApplyFilterFromFilterCreator(Func<bool, Action<BytesView>> creator)
        {
            AddWriteFilter(creator(true));
            AddReadFilter(creator(false));
        }

        public void ApplyDeflateFilter()
        {
            AddWriteFilter(GetDeflateFilter(true));
            AddReadFilter(GetDeflateFilter(false));
        }

        [Obsolete]
        public static Action<BytesView> GetXORFilter(byte[] key)
        {
            return (bv) => {
                var tlen = bv.tlen;
                for (int i = 0; i < tlen; i++) {
                    bv[i] ^= key[i % key.Length];
                }
            };
        }

        [Obsolete]
        public static Action<BytesView> GetXORFilter2(byte[] key)
        {
            int lastpostion = 0;
            return (bv) => {
                int i = lastpostion;
                int tlen = bv.tlen;
                for (; i < tlen; i++) {
                    bv[i] ^= key[i % key.Length];
                }
                lastpostion = i % key.Length;
            };
        }

        [Obsolete]
        public static Action<BytesView> GetXORFilter3(byte[] key)
        {
            int lastpostion = 0;
            byte pass = 0;
            return (bv) => {
                int ki = lastpostion;
                int tlen = bv.tlen;
                for (int i = 0; i < tlen; i++) {
                    bv[i] ^= (byte)(key[ki++] + pass);
                    if (ki == key.Length) {
                        ki = 0;
                        pass++;
                    }
                }
                lastpostion = ki;
            };
        }

        [Obsolete]
        public static Action<BytesView> GetAesFilter(bool isEncrypt, byte[] iv, byte[] key)
        {
            int keySize = key.Length * 8, blockSize = iv.Length * 8;
            int blockBytesSize = blockSize / 8;
            byte[] buf = new byte[blockSize / 8];
            BytesView bvBuf = new BytesView(buf);
            var aesalg = Aes.Create();
            aesalg.Padding = PaddingMode.PKCS7;
            aesalg.KeySize = keySize;
            aesalg.BlockSize = blockSize;
            aesalg.IV = iv;
            aesalg.Key = key;
            return (bv) => {
                if (bv.len == 0)
                    return;
                var pos = 0;
                if (isEncrypt) {
                    using (var ms = new MemoryStream(bv.bytes, bv.offset, bv.len))
                    using (var cryStream = new CryptoStream(ms, aesalg.CreateEncryptor(), CryptoStreamMode.Read)) {
                        int read;
                        int oldbytesSize = bv.len - (bv.len % blockBytesSize);
                        bv.len = oldbytesSize;
                        bv.nextNode = bvBuf;
                        while ((read = cryStream.Read(bv.bytes, bv.offset + pos, oldbytesSize - pos)) > 0) {
                            pos += read;
                        }
                        while ((read = cryStream.Read(buf, pos - oldbytesSize, buf.Length - (pos - oldbytesSize))) > 0) {
                            pos += read;
                        }
                    }
                } else {
                    using (var cryStream = new CryptoStream(new MemoryStream(bv.bytes, bv.offset, bv.len), aesalg.CreateDecryptor(), CryptoStreamMode.Read)) {
                        int read;
                        while ((read = cryStream.Read(buf, 0, blockBytesSize)) > 0) {
                            for (int i = 0; i < read; i++) {
                                bv.bytes[bv.offset + pos++] = buf[i];
                            }
                        }
                        bv.len = pos;
                    }
                }
            };
        }

        public static Action<BytesView> GetAesFilter2(bool isEncrypting, byte[] key)
        {
            int keySize = key.Length * 8, blockSize = 128;
            int blockBytesSize = blockSize / 8;
            byte[] buf = new byte[blockSize / 8];
            BytesView bvBuf = new BytesView(buf);
            var aesalg = new AesCryptoServiceProvider();
            aesalg.Padding = PaddingMode.PKCS7;
            //aesalg.Mode = CipherMode.CBC;
            aesalg.KeySize = keySize;
            aesalg.BlockSize = blockSize;
            aesalg.Key = key;
            bool firstPacket = true;
            if (isEncrypting) {
                return (bv) => {
                    if (firstPacket) {
                        firstPacket = false;
                        aesalg.GenerateIV();
                        var tmp = bv.Clone();
                        bv.Set(aesalg.IV);
                        bv.nextNode = tmp;
                        bv = tmp;
                    }
                    if (bv.len == 0)
                        return;
                    var pos = 0;
                    using (var ms = new MemoryStream(bv.bytes, bv.offset, bv.len))
                    using (var cryStream = new CryptoStream(ms, aesalg.CreateEncryptor(), CryptoStreamMode.Read)) {
                        int read;
                        int oldbytesSize = bv.len - (bv.len % blockBytesSize);
                        bv.len = oldbytesSize;
                        bv.nextNode = bvBuf;
                        while ((read = cryStream.Read(bv.bytes, bv.offset + pos, oldbytesSize - pos)) > 0) {
                            pos += read;
                        }
                        while ((read = cryStream.Read(buf, pos - oldbytesSize, buf.Length - (pos - oldbytesSize))) >
                               0) {
                            pos += read;
                        }
                        aesalg.IV = buf;
                    }
                };
            } else {
                return (bv) => {
                    if (firstPacket) {
                        firstPacket = false;
                        aesalg.IV = bv.GetBytes(0, blockBytesSize);
                        //bv.offset += blockBytesSize;
                        for (int i = 0; i < bv.len - blockBytesSize; i++) {
                            bv[i] = bv[i + blockBytesSize];
                        }
                        bv.len -= blockBytesSize;
                    }
                    if (bv.len == 0)
                        return;
                    var pos = 0;
                    using (var cryStream = new CryptoStream(new MemoryStream(bv.bytes, bv.offset, bv.len),
                        aesalg.CreateDecryptor(), CryptoStreamMode.Read)) {
                        int read;
                        int lastBlockPos = bv.len - blockBytesSize;
                        for (int i = 0; i < blockBytesSize; i++) {
                            buf[i] = bv[lastBlockPos + i];
                        }
                        aesalg.IV = buf;
                        while ((read = cryStream.Read(bv.bytes, bv.offset + pos, bv.len - pos)) > 0) {
                            pos += read;
                        }
                        bv.len = pos;
                    }
                };
            }
        }

        const int blocksPerPass = 16;

        private static readonly byte[] zeroesBytes = new byte[16 * blocksPerPass];

        public static Action<BytesView> GetAesStreamFilter(bool isEncrypt, byte[] key) // is actually AES OFB
        {
            int keySize = key.Length * 8, blockSize = 128;
            int blockBytesSize = blockSize / 8;
            int encryptedZerosSize = blockBytesSize;
            byte[] buf = new byte[blockSize / 8];
            BytesView bvBuf = new BytesView(buf);
            var aesalg = new AesCryptoServiceProvider();
            aesalg.Mode = CipherMode.CBC; // to generate OFB keystream, use CBC with all zeros byte array as input.
            aesalg.KeySize = keySize;
            aesalg.BlockSize = blockSize;
            aesalg.Key = key;
            ICryptoTransform enc = null;
            byte[] keystreamBuffer = null;
            int keystreamBufferPos = 0;
            return bv => {
                if (enc == null) {
                    if (isEncrypt) {
                        bv.nextNode = bv.Clone();
                        bv.Set(aesalg.IV);
                        bv = bv.nextNode;
                    } else {
                        aesalg.IV = bv.GetBytes(0, blockBytesSize);
                        for (int i = 0; i < bv.len - blockBytesSize; i++) {
                            bv[i] = bv[i + blockBytesSize];
                        }
                        bv.len -= blockBytesSize;
                    }
                    enc = aesalg.CreateEncryptor();
                    if (enc.CanTransformMultipleBlocks) {
                        encryptedZerosSize *= blocksPerPass;
                    }
                    keystreamBuffer = new byte[encryptedZerosSize];
                    keystreamBufferPos = encryptedZerosSize;
                }
                unsafe {
                    fixed (byte* ksBuf = keystreamBuffer)
                        do {
                            var pos = bv.offset;
                            var end = pos + bv.len;
                            if (bv.bytes == null)
                                throw new ArgumentNullException("bv.bytes");
                            if (bv.bytes.Length < end)
                                throw new ArgumentException("bv.bytes.Length < offset + len");
                            fixed (byte* bytes = bv.bytes)
                                while (pos < end) {
                                    var remainningTmp = encryptedZerosSize - keystreamBufferPos;
                                    if (remainningTmp == 0) {
                                        remainningTmp = encryptedZerosSize;
                                        keystreamBufferPos = 0;
                                        enc.TransformBlock(zeroesBytes, 0, encryptedZerosSize, keystreamBuffer, 0);
                                    }
                                    var tmpEnd = pos + remainningTmp;
                                    var thisEnd = end < tmpEnd ? end : tmpEnd;
                                    var thisCount = thisEnd - pos;
                                    NaiveUtils.XorBytesUnsafe(ksBuf + keystreamBufferPos, bytes + pos, thisCount);
                                    keystreamBufferPos += thisCount;
                                    pos += thisCount;
                                }
                            bv = bv.nextNode;
                        }
                        while (bv != null);
                }
            };
        }

        public static Action<BytesView> GetStreamFilterFromIVEncryptor(bool isEncrypt, IIVEncryptor iVEncryptor)
        {
            return GetStreamFilterFromIVEncryptor(isEncrypt, iVEncryptor, false);
        }

        public static Action<BytesView> GetStreamFilterFromIVEncryptor(bool isEncrypt, IIVEncryptor iVEncryptor, bool ivOK)
        {
            return (bv) => {
                if (!ivOK) {
                    ivOK = true;
                    if (isEncrypt) {
                        bv.nextNode = bv.Clone();
                        bv.Set(iVEncryptor.IV);
                        bv = bv.nextNode;
                    } else {
                        int iVLength = iVEncryptor.IVLength;
                        iVEncryptor.IV = bv.GetBytes(0, iVLength, true);
                        bv.Sub(iVLength);
                    }
                }
                foreach (var item in bv) {
                    iVEncryptor.Update(new BytesSegment(item));
                }
            };
        }

        public static Action<BytesView> GetDeflateFilter(bool isCompress)
        {
            return (bv) => {
                using (var tostream = new MemoryStream()) {
                    using (var ds = new DeflateStream(
                        isCompress ? tostream : new MemoryStream(bv.bytes, bv.offset, bv.len),
                        isCompress ? CompressionMode.Compress : CompressionMode.Decompress)) {
                        if (isCompress) {
                            ds.Write(bv.bytes, bv.offset, bv.len);
                            ds.Flush();
                        } else {
                            NaiveUtils.StreamToStream(ds, tostream);
                        }
                    }
                    bv.Set(tostream.ToArray());
                }
            };
        }

        public static Action<BytesView> GetHashFilter(bool isWrite)
        {
            return (bv) => {
                if (isWrite) {
                    if (bv == null || bv.tlen == 0)
                        return;
                    var bytes = bv.GetBytes();
                    using (var alg = new MD5CryptoServiceProvider()) {
                        var b = alg.ComputeHash(bytes);
                        bv.lastNode.nextNode = new BytesView(b);
                    }
                } else {
                    if (bv == null || bv.tlen == 0)
                        return;
                    using (var alg = new MD5CryptoServiceProvider()) {
                        var hb = alg.HashSize / 8;
                        var bytes = bv.GetBytes(0, bv.tlen - hb);
                        var hash = alg.ComputeHash(bytes);
                        var rhash = bv.GetBytes(bv.tlen - hb, hb);
                        if (!hash.SequenceEqual(rhash)) {
                            Logging.error("wrong hash!");
                            throw new Exception("wrong hash");
                        }
                        bv.len -= hb;
                    }
                }
            };
        }
    }
}
