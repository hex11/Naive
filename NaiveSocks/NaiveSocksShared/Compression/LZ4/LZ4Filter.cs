using System;
using System.Collections.Generic;
using System.Text;
using LZ4pn;
using Naive.HttpSvr;

namespace LZ4pn
{
    class LZ4Filter
    {
        // format:
        // [0x00] [uncompressed data]
        // OR
        // [x = 0x01 ~ 0xfe] [compressed data] (uncompressed data size = x - 0x01)
        // OR
        // [0xff] [(4 bytes) uncompressed data size] [compressed data]

        public static Action<BytesView> GetFilter(bool isWriting) => GetFilter(isWriting, false);
        public static Action<BytesView> GetFilter(bool isWriting, bool alwaysCompress)
        {
            const int InitCompressingChances = 4;
            if (isWriting) {
                int compressingChances = InitCompressingChances;
                return (x) => {
                    var tlen = x.tlen;
                    if (tlen <= 0)
                        return;
                    bool compress = true;
                    if (tlen < 256) {
                        compress = false;
                    } else if (compressingChances <= 0) {
                        if (--compressingChances <= -64) {
                            compressingChances = 2;
                        }
                        compress = false;
                    }
                    byte[] header;
                    var headerCur = 0;
                    if (!compress || tlen < 0xff - 1 - 1) {
                        header = new byte[1];
                        header[headerCur++] = compress ? (byte)(tlen + 1) : (byte)0;
                    } else {
                        header = new byte[5];
                        header[headerCur++] = 0xff;
                        for (int i = 4 - 1; i >= 0; i--) {
                            header[headerCur++] = (byte)(tlen >> (i * 8));
                        }
                    }
                    if (compress) {
                        var input = x.GetBytes(0, tlen);
                        int outputLength;
                        var compressedData = IntPtr.Size < 8
                            ? LZ4Codec.Encode32(input, 0, tlen, out outputLength)
                            : LZ4Codec.Encode64(input, 0, tlen, out outputLength);
                        if (outputLength + (headerCur - 1) < tlen) {
                            x.nextNode = new BytesView(compressedData, 0, outputLength);
                            compressingChances = InitCompressingChances;
                        } else {
                            // if compressed size >= original size, send original data.
                            x.nextNode = input;
                            header[0] = 0;
                            headerCur = 1;
                            if (!alwaysCompress)
                                compressingChances--;
                        }
                    } else {
                        x.nextNode = x.Clone();
                    }
                    x.Set(header, 0, headerCur);
                };
            } else {
                return (x) => {
                    var tlen = x.tlen;
                    if (tlen <= 0)
                        return;
                    var cur = 0;
                    var firstByte = x[cur++];
                    if (firstByte == 0x00) {
                        x.SubSelf(1);
                        return;
                    }
                    int outputLength;
                    if (firstByte < 0xff) {
                        outputLength = firstByte - 0x01;
                    } else {
                        outputLength = 0;
                        for (int i = 4 - 1; i >= 0; i--) {
                            outputLength |= x[cur++] << (i * 8);
                        }
                    }
                    byte[] input; int inputOffset = 0, inputLength = tlen - cur;
                    if (x.nextNode != null) {
                        input = x.GetBytes(cur, tlen - cur);
                    } else {
                        input = x.bytes;
                        inputOffset = x.offset + cur;
                    }
                    x.Set(IntPtr.Size < 8
                        ? LZ4Codec.Decode32(input, inputOffset, inputLength, outputLength)
                        : LZ4Codec.Decode64(input, inputOffset, inputLength, outputLength));
                    x.nextNode = null;
                };
            }
        }
    }
}
