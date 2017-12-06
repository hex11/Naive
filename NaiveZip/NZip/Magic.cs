using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Naive
{
    class Magic
    {
        static string magic = "D0C415F9-4125-412D-943C-37E78AFAE221_\0_                                _\0";
        static byte[] magic_bytes => magic.encode("utf-16");
        public static char magic_char => magic[magic.Length - 1];
        static byte[] genMagicBytes(char magicCh, string magicB = null) => genMagicChars(magicCh, magicB).encode("utf-16");
        static char[] genMagicChars(char magicCh, string magicB = null)
        {
            var chars = magic.ToCharArray();
            chars[chars.Length - 1] = magicCh;
            if (magicB != null) {
                if (magicB.Length > magicBmaxLen)
                    throw new Exception();
                chars[magicBcountPos] = (char)magicB.Length;
                magicB = magicB.PadRight(magicBmaxLen, ' ');
                for (int i = 0; i < magicBmaxLen; i++) {
                    chars[magicBstart + i] = magicB[i];
                }
            }
            return chars;
        }

        static int magicBstart => magic.Length - 2 - magicBmaxLen;
        static int magicBmaxLen => 32;
        static int magicBcountPos => magicBstart - 2;

        public static string getMagicB()
        {
            return magic.Substring(magicBstart, magic[magicBcountPos]);
        }

        public static byte[] genExe(char magicCh, bool? isGuiMode = null, string magicB = null)
        {
            var exe = File.ReadAllBytes(GetSelfPath());
            if (isGuiMode != null)
                setSubsystem(exe, isGuiMode.Value ? (byte)2 : (byte)3);
            var strbytes = magic_bytes;
            var strbytes2 = genMagicBytes(magicCh, magicB);
            var pos = exe.Locate(strbytes)[0];
            for (int i = 0; i < strbytes2.Length; i++) {
                exe[pos + i] = strbytes2[i];
            }
            return exe;
        }

        public static string GetSelfPath()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().Location;
        }

        public static void WriteExe(string path, char magicCh, bool isGuiMode = true)
        {
            var exe = genExe(magicCh, isGuiMode);
            var tmpFilePath = path + ".new";
            File.WriteAllBytes(tmpFilePath, exe);
            MagicUtil.MoveOrReplace(path, tmpFilePath);
        }

        // https://msdn.microsoft.com/en-us/library/windows/desktop/ms680547(v=vs.85).aspx
        public static void setSubsystem(byte[] pe, byte subsystem)
        {
            uint indexSubsystem = locateSubsystemIndex(pe);
            pe[indexSubsystem] = subsystem;
        }
        public static byte getSubsystem(byte[] pe)
        {
            uint indexSubsystem = locateSubsystemIndex(pe);
            return pe[indexSubsystem];
        }

        private static uint locateSubsystemIndex(byte[] pe)
        {
            if (pe[0] != 'M' || pe[1] != 'Z') {
                throw new Exception("MS-DOS header not found.");
            }
            var indexPE = BitConverter.ToUInt32(pe, 0x3c);
            if (
                pe[indexPE] != 'P' ||
                pe[indexPE + 1] != 'E' ||
                pe[indexPE + 2] != '\0' ||
                pe[indexPE + 3] != '\0'
            ) {
                throw new Exception("PE magic number not found.");
            }
            var indexSubsystem = indexPE
                                 + 4 // "PE\0\0"
                                 + 20 // COFF header
                                 + 68;
            return indexSubsystem;
        }
    }

    static class MagicUtil
    {

        private static string getTempFilePath(string path)
        {
            return path + ".new";
        }

        public static void WriteAllTextSafe(string path, string text, Encoding encoding = null)
        {
            var newfilepath = getTempFilePath(path);
            File.WriteAllText(newfilepath, text, encoding ?? Encoding.UTF8);
            MoveOrReplace(path, newfilepath);
        }

        public static void WriteAllLinesSafe(string path, IEnumerable<string> text, Encoding encoding = null)
        {
            var newfilepath = getTempFilePath(path);
            File.WriteAllLines(newfilepath, text, encoding ?? Encoding.UTF8);
            MoveOrReplace(path, newfilepath);
        }

        public static void MoveOrReplace(string dest, string source)
        {
            if (File.Exists(dest))
                File.Replace(source, dest, null);
            else
                File.Move(source, dest);
        }

        public static byte[] encode(this string str, string code)
        {
            return Encoding.GetEncoding(code).GetBytes(str);
        }


        public static byte[] encode(this char[] str, string code)
        {
            return Encoding.GetEncoding(code).GetBytes(str);
        }


        public static string decode(this byte[] bytes, string code)
        {
            return Encoding.GetEncoding(code).GetString(bytes);
        }
    }

    public static class ByteArrayRocks
    {

        static readonly int[] Empty = new int[0];

        public static int[] Locate(this byte[] self, byte[] candidate)
        {
            if (IsEmptyLocate(self, candidate))
                return Empty;

            var list = new List<int>();

            for (int i = 0; i < self.Length; i++) {
                if (!IsMatch(self, i, candidate))
                    continue;

                list.Add(i);
            }

            return list.Count == 0 ? Empty : list.ToArray();
        }

        static bool IsMatch(byte[] array, int position, byte[] candidate)
        {
            if (candidate.Length > (array.Length - position))
                return false;

            for (int i = 0; i < candidate.Length; i++)
                if (array[position + i] != candidate[i])
                    return false;

            return true;
        }

        static bool IsEmptyLocate(byte[] array, byte[] candidate)
        {
            return array == null
                || candidate == null
                || array.Length == 0
                || candidate.Length == 0
                || candidate.Length > array.Length;
        }
    }
}
