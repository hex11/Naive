using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Naive
{
    public class Util
    {
        public static void StreamToStream(Stream from, Stream to, long size = -1, int bs = 16 * 1024)
        {
            if (size == 0)
                return;
            if (size < -1)
                throw new ArgumentOutOfRangeException(nameof(size));
            int bufferSize = (int)(size == -1 ? bs :
                                    size < bs ? size : bs);
            byte[] buffer = new byte[bufferSize];
            if (size == -1) {
                while (true) {
                    int read = from.Read(buffer, 0, bufferSize);
                    if (read == 0)
                        break;
                    to.Write(buffer, 0, read);
                }
            } else {
                while (true) {
                    int read = from.Read(buffer, 0, (int)(size > bufferSize ? bufferSize : size));
                    if (read == 0)
                        throw new EndOfStreamException();
                    to.Write(buffer, 0, read);
                    size -= read;
                    if (size <= 0)
                        return;
                }
            }
        }
    }
}
