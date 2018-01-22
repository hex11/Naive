﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;
using System.Collections.Generic;

namespace NaiveSocks
{
    public static class SS
    {
        public static SymmetricAlgorithm GetEcbAlg(byte[] realKey)
        {
            var provider = new AesCryptoServiceProvider();
            provider.Mode = CipherMode.ECB;
            provider.Padding = PaddingMode.None;
            provider.KeySize = realKey.Length * 8;
            provider.Key = realKey;
            return provider;
        }

        // copy & plste from ss-csharp. (modified)
        public static void BytesToKey(byte[] password, byte[] key)
        {
            var result = new byte[16 + password.Length];
            int i = 0;
            byte[] md5sum = null;
            using (var md5 = new MD5CryptoServiceProvider())
                while (i < key.Length) {
                    if (i == 0) {
                        md5sum = md5.ComputeHash(password);
                    } else {
                        md5sum.CopyTo(result, 0);
                        password.CopyTo(result, md5sum.Length);
                        md5sum = md5.ComputeHash(result);
                    }
                    md5sum.CopyTo(key, i);
                    i += md5sum.Length;
                }
        }

        public static byte[] StringToKey(string str, int length)
        {
            var bytes = new byte[length];
            BytesToKey(NaiveUtils.UTF8Encoding.GetBytes(str), bytes);
            return bytes;
        }

        public static Cipher GetCipherByName(string cipherName)
        {
            if (Ciphers.TryGetValue(cipherName, out var cipher) == false) {
                throw new Exception($"encryption '{cipherName}' is not supported.\n(supported: {string.Join(", ", SS.Ciphers.Keys)})");
            }
            return cipher;
        }

        public static Dictionary<string, Cipher> Ciphers = new Dictionary<string, Cipher> {
            ["aes-128-ctr"] = new Cipher(Cipher.Modes.AesCtr, 16),
            ["aes-192-ctr"] = new Cipher(Cipher.Modes.AesCtr, 24),
            ["aes-256-ctr"] = new Cipher(Cipher.Modes.AesCtr, 32),
            ["aes-128-cfb"] = new Cipher(Cipher.Modes.AesCfb, 16),
            ["aes-192-cfb"] = new Cipher(Cipher.Modes.AesCfb, 24),
            ["aes-256-cfb"] = new Cipher(Cipher.Modes.AesCfb, 32),
            ["chacha20-ietf"] = new Cipher(Cipher.Modes.Chacha20Ietf, 32),
        };

        public class Cipher
        {
            public enum Modes
            {
                AesCtr,
                AesCfb,
                Chacha20Ietf,
            }

            public Cipher(Modes encryptionMode, int keySize)
            {
                KeySize = keySize;
                Mode = encryptionMode;
            }

            public int KeySize { get; }
            public Modes Mode { get; }

            public Func<IMyStream, IMyStream> GetEncryptionStreamFunc(string key)
                => GetEncryptionStreamFunc(StringToKey(key, KeySize));

            public Func<IMyStream, IMyStream> GetEncryptionStreamFunc(byte[] key)
            {
                return baseStream => {
                    if (Mode == Modes.AesCtr) {
                        var alg = GetEcbAlg(key);
                        return new IvEncryptStream(
                            baseStream,
                            new CtrEncryptor(alg.CreateEncryptor()),
                            new CtrEncryptor(alg.CreateEncryptor()));
                    } else if (Mode == Modes.AesCfb) {
                        var alg = GetEcbAlg(key);
                        return new IvEncryptStream(
                            baseStream,
                            new CfbEncryptor(alg.CreateEncryptor(), false),
                            new CfbEncryptor(alg.CreateEncryptor(), true));
                    } else if (Mode == Modes.Chacha20Ietf) {
                        return new IvEncryptStream(
                            baseStream,
                            new ChaCha20IetfEncryptor(key),
                            new ChaCha20IetfEncryptor(key));
                    } else {
                        throw new Exception("No such cipher.");
                    }
                };
            }
        }

        public static byte[] SSHkdfInfo = NaiveUtils.UTF8Encoding.GetBytes("ss-subkey");

        // HKDF_SHA1
        // libsscrypto/hkdf.c mbedtls_hkdf
        public static byte[] GetAeadSubkey(byte[] key, byte[] salt, byte[] info, int outputLen)
        {
            byte[] prk;
            using (var h = new HMACSHA1(salt)) {
                prk = h.ComputeHash(key);
            }
            var N = outputLen / prk.Length;
            if (outputLen % prk.Length != 0)
                N++;
            var T = new byte[0];
            var c = new byte[1];
            var output = new byte[outputLen];
            var cur = 0;
            using (var h = new HMACSHA1(prk)) {
                for (int i = 0; i < N; i++) {
                    h.TransformBlock(T, 0, T.Length, T, 0);
                    h.TransformBlock(info, 0, info.Length, info, 0);
                    c[0] = (byte)i;
                    T = h.TransformFinalBlock(c, 0, 1);
                    Buffer.BlockCopy(T, 0, output, cur, (i < N) ? T.Length : outputLen - cur);
                    cur += T.Length;
                }
            }
            return output;
        }
    }

    public interface IMac
    {
        void Update(BytesSegment bs);
        byte[] IV { get; set; }
    }

    public abstract class IVEncryptorBase : IIVEncryptor
    {
        protected byte[] _iv;
        public byte[] IV
        {
            get {
                if (_iv == null) {
                    using (var rng = new RNGCryptoServiceProvider()) {
                        var buf = new byte[IVLength];
                        rng.GetNonZeroBytes(buf);
                        IV = buf;
                    }
                }
                return _iv;
            }
            set {
                _iv = value;
                IVSetup(_iv);
            }
        }

        protected abstract void IVSetup(byte[] IV);

        public abstract int IVLength { get; }

        public abstract void Update(BytesSegment bs);
    }

    public class CtrEncryptor : IVEncryptorBase
    {
        public byte[] Counter;
        private byte[] counterBlocks;
        public ICryptoTransform EcbTransform;
        private int blockSize;
        private int encryptedCounterSize;
        private byte[] encryptedCounterBlocks;
        private int encryptedCounterPos;

        public override int IVLength { get; }

        public CtrEncryptor(ICryptoTransform ecbTransform)
        {
            EcbTransform = ecbTransform;
            IVLength = EcbTransform.InputBlockSize;
        }

        protected override void IVSetup(byte[] IV)
        {
            var counter = IV;
            counterBlocks = Counter = counter;
            encryptedCounterSize = blockSize = IVLength;
            if (counter.Length != blockSize)
                throw new Exception("counter size != block size");
            if (EcbTransform.CanTransformMultipleBlocks) {
                encryptedCounterSize *= 16;
                counterBlocks = new byte[encryptedCounterSize];
            }
            encryptedCounterBlocks = new byte[encryptedCounterSize];
            encryptedCounterPos = encryptedCounterSize;
        }

        public override unsafe void Update(BytesSegment bs)
        {
            int i = bs.Offset;
            int end = i + bs.Len;
            var realEncryptedCounterPos = this.encryptedCounterPos;
            this.encryptedCounterPos = (realEncryptedCounterPos + bs.Len) % encryptedCounterSize;
            fixed (byte* encCtr = encryptedCounterBlocks)
            fixed (byte* bytes = bs.Bytes)
                while (i < end) {
                    var unusedEncryptedCounter = encryptedCounterSize - realEncryptedCounterPos;
                    if (unusedEncryptedCounter == 0) {
                        realEncryptedCounterPos = 0;
                        for (int j = 0; j < encryptedCounterSize; j += blockSize) {
                            Buffer.BlockCopy(Counter, 0, counterBlocks, j, blockSize);
                            IncrementCounter();
                        }
                        EcbTransform.TransformBlock(counterBlocks, 0, encryptedCounterSize, encryptedCounterBlocks, 0);
                        unusedEncryptedCounter = encryptedCounterSize;
                    }
                    var blockEnd = i + unusedEncryptedCounter;
                    int blocksEnd = (blockEnd < end) ? blockEnd : end;
                    var count = blocksEnd - i;

                    NaiveUtils.XorBytesUnsafe(encCtr + realEncryptedCounterPos, bytes + i, count);
                    realEncryptedCounterPos += count;
                    i = blocksEnd;
                }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void IncrementCounter()
        {
            for (var i = Counter.Length - 1; i >= 0; i--) {
                if (++Counter[i] != 0)
                    break;
            }
        }
    }

    public class CfbEncryptor : IVEncryptorBase
    {
        public CfbEncryptor(ICryptoTransform EcbTransform, bool isEncrypt)
        {
            this.EcbTransform = EcbTransform;
            IsEncrypting = isEncrypt;
            IVLength = EcbTransform.InputBlockSize;
        }

        public override int IVLength { get; }

        public ICryptoTransform EcbTransform { get; }
        public bool IsEncrypting { get; }

        int feedingPos;
        byte[] toBeFeedBack;
        byte[] feedingBack;

        protected override void IVSetup(byte[] IV)
        {
            toBeFeedBack = IV;
            feedingPos = toBeFeedBack.Length;
            feedingBack = (IsEncrypting) ? toBeFeedBack : new byte[toBeFeedBack.Length];
        }

        public override void Update(BytesSegment bs)
        {
            bs.CheckAsParameter();
            var pos = bs.Offset;
            var end = pos + bs.Len;
            while (pos < end) {
                var remainningFeed = toBeFeedBack.Length - feedingPos;
                if (remainningFeed == 0) {
                    feedingPos = 0;
                    remainningFeed = toBeFeedBack.Length;
                    EcbTransform.TransformBlock(toBeFeedBack, 0, toBeFeedBack.Length, feedingBack, 0);
                }
                var feedEnd = pos + remainningFeed;
                var thisBlockEnd = (feedEnd < end) ? feedEnd : end;
                var count = thisBlockEnd - pos;
                if (IsEncrypting) {
                    NaiveUtils.XorBytes(feedingBack, feedingPos, bs.Bytes, pos, count);
                    Buffer.BlockCopy(bs.Bytes, pos, toBeFeedBack, feedingPos, count);
                } else {
                    Buffer.BlockCopy(bs.Bytes, pos, toBeFeedBack, feedingPos, count);
                    NaiveUtils.XorBytes(feedingBack, feedingPos, bs.Bytes, pos, count);
                }
                pos += count;
                feedingPos += count;
            }
        }
    }

    public class IvEncryptStream : IMyStream
    {
        public IMyStream BaseStream { get; }
        public IIVEncryptor recvHelper, sendHelper;
        private bool ivSent, ivReceived;

        public IvEncryptStream(IMyStream baseStream, IIVEncryptor recv, IIVEncryptor send)
        {
            BaseStream = baseStream;
            recvHelper = recv;
            sendHelper = send;
        }

        public MyStreamState State => BaseStream.State;

        public Task Close()
        {
            return BaseStream.Close();
        }

        public Task Shutdown(SocketShutdown direction)
        {
            return BaseStream.Shutdown(direction);
        }

        public async Task<int> ReadAsync(BytesSegment bv)
        {
            if (!ivReceived) {
                var buf = new BytesSegment(new byte[recvHelper.IVLength]);
                await BaseStream.ReadAllAsync(buf, buf.Len).CAF();
                recvHelper.IV = buf.Bytes;
                ivReceived = true;
            }
            var read = await BaseStream.ReadAsync(bv).CAF();
            if (read > 0) {
                bv.Len = read;
                recvHelper.Update(bv);
            }
            return read;
        }

        public async Task WriteAsync(BytesSegment bv)
        {
            if (!ivSent) {
                var iv = sendHelper.IV;
                await BaseStream.WriteAsync(new BytesSegment(iv)).CAF();
                ivSent = true;
            }
            sendHelper.Update(bv);
            await BaseStream.WriteAsync(bv).CAF();
        }

        public async Task FlushAsync()
        {
            await BaseStream.FlushAsync();
        }

        public override string ToString()
        {
            return $"{{IVEncryptor on {BaseStream}}}";
        }
    }

    public class AeadEncryptStream : IMyStream
    {
        public IMyStream BaseStream { get; }
        public IIVEncryptor recvHelper, sendHelper;
        public IMac recvMac, sendMac;
        private bool ivSent, ivReceived;

        public AeadEncryptStream()
        {
        }

        public MyStreamState State => BaseStream.State;

        public Task Close()
        {
            return BaseStream.Close();
        }

        public Task Shutdown(SocketShutdown direction)
        {
            return BaseStream.Shutdown(direction);
        }

        public async Task<int> ReadAsync(BytesSegment bv)
        {
            throw new NotImplementedException();
        }

        public async Task WriteAsync(BytesSegment bv)
        {
            throw new NotImplementedException();
        }

        public async Task FlushAsync()
        {
            await BaseStream.FlushAsync();
        }

        public override string ToString()
        {
            return $"{{AeadEncryptor on {BaseStream}}}";
        }
    }
}