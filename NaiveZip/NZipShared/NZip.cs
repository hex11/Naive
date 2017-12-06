using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO.Compression;
using System.Collections;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using FSZSimpleJsonNS;
using System.Linq;

namespace NZip
{

    public class NZ : IDisposable
    {
        public Func<Stream> StreamProvider;

        public long posOffset;
        PkgHeader header;
        Stream stream;
        object streamlocker = new object();
        Hashtable files;

        public int FileCount => files.Count;

        public static NZ FromFile(string path) => FromStream(() => File.OpenRead(path));
        public static NZ FromBytes(byte[] bytes) => FromStream(() => new MemoryStream(bytes));
        public static NZ FromStream(Func<Stream> streamProvider) => FromStream(streamProvider, 0);
        public static NZ FromStream(Func<Stream> streamProvider, int offset)
        {
            var fsz = FromStream(streamProvider(), offset);
            fsz.StreamProvider = streamProvider;
            return fsz;
        }

        public static NZ FromStream(Stream stream) => FromStream(stream, 0);
        public static NZ FromStream(Stream stream, int posOffset)
        {
            var ret = new NZ();
            ret.posOffset = posOffset;
            ret.stream = stream;
            ret.init();
            return ret;
        }

        private NZ()
        {
        }

        public void Dispose()
        {
            stream?.Dispose();
        }

        void init()
        {
            stream.Position = posOffset;
            var headerbegin = BitConverter.ToInt32(readbytes(stream, sizeof(int)), 0);
            header = DeserialzePkgHeader(headerbegin);
            files = new Hashtable(header.Files.Length);
            foreach (var item in header.Files) {
                files.Add(item.name, item);
            }
        }

        public void Close() => stream.Close();

        public NZFileinfo[] GetFiles()
        {
            var files = new NZFileinfo[header.Files.Length];
            for (int i = 0; i < header.Files.Length; i++) {
                files[i] = header.Files[i];
            }
            return files;
        }

        public string[] GetFilenames()
        {
            var files = new string[header.Files.Length];
            for (int i = 0; i < header.Files.Length; i++) {
                files[i] = header.Files[i].name;
            }
            return files;
        }

        public NZFileinfo GetFile(string path)
        {
            var file = files[path] as NZFileinfo;
            return file;
        }

        public byte[] GetFileBytes(string path)
        {
            var fi = GetFile(path);
            return fi != null ? GetFileBytes(fi) : null;
        }

        public byte[] GetFileBytes(NZFileinfo fileInfo)
        {
            byte[] bytes = new byte[fileInfo.length];
            var ms = new MemoryStream(bytes);
            WriteFileTo(fileInfo, ms, false);
            return bytes;
        }

        public void WriteFileTo(string path, Stream to)
        {
            var file = GetFile(path);
            if (file == null)
                throw new Exception("NZipFile not found");
            WriteFileTo(file, to);
        }

        public void WriteFileTo(NZFileinfo file, Stream to, bool writeRawGzipData = false)
        {
            if (StreamProvider == null) {
                lock (streamlocker) {
                    stream.Position = posOffset + file.pos;
                    if (writeRawGzipData) {
                        CopyStream(stream, to, file.ziplen);
                    } else {
                        using (var gz = new GZipStream(stream, CompressionMode.Decompress, true)) {
                            CopyStream(gz, to, file.length);
                        }
                    }
                }
            } else {
                using (var stream = StreamProvider()) {
                    stream.Position = posOffset + file.pos;
                    if (writeRawGzipData) {
                        CopyStream(stream, to, file.ziplen);
                    } else {
                        using (var gz = new GZipStream(stream, CompressionMode.Decompress, true)) {
                            CopyStream(gz, to, file.length);
                        }
                    }
                }
            }
        }

        public async Task WriteFileToAsync(NZFileinfo file, Stream to, bool writeRawGzipData = false)
        {
            if (StreamProvider == null) {
                throw new Exception("async is not suppoted without StreamProvider.");
            } else {
                using (var stream = StreamProvider()) {
                    stream.Position = posOffset + file.pos;
                    if (writeRawGzipData) {
                        await CopyStreamAsync(stream, to, file.ziplen);
                    } else {
                        using (var gz = new GZipStream(stream, CompressionMode.Decompress, true)) {
                            await CopyStreamAsync(gz, to, file.length);
                        }
                    }
                }
            }
        }

        public DateTime CreateTime
        {
            get {
                if (header.CreateTime < 0) {
                    return DateTime.MinValue;
                }
                return ConvertIntDateTime(header.CreateTime);
            }
        }

        public static void Create(Stream output, string[] inputfiles, string rootdir = null, TextWriter debugoutput = null)
        {
            var files = from x in inputfiles
                        let fi = new FileInfo(x)
                        select new AddingFSFile() {
                            file = () => File.OpenRead(x),
                            lwt = fi.LastWriteTimeUtc,
                            name = getName(rootdir, fi)
                        };
            Create(output, files, debugoutput);
        }

        public static void Create(Stream output, IEnumerable<AddingFSFile> filesToAdd, TextWriter debugoutput = null)
        {
            var header = new PkgHeader();
            header.NZipMinVersion = 1;
            header.NZipVersion = 2;
            var files = new List<NZFileinfo>();
            var beginPosition = output.Position;
            output.Write(BitConverter.GetBytes(-1), 0, sizeof(int));
            int lengthsum = (int)output.Position;
            int i = 0;
            foreach (var x in filesToAdd) {
                var fsfi = new NZFileinfo();
                fsfi.name = x.name;
                debugoutput?.WriteLine("[{1}/{2}] \"{0}\"", fsfi.name, i + 1, (filesToAdd as ICollection)?.Count.ToString() ?? "NaN");
#if DOTNET45
                using (var gz = new GZipStream(output, CompressionLevel.Optimal, true)) {
#else
                using (var gz = new GZipStream(output, CompressionMode.Compress, true)) {
#endif
                    using (var file = x.file()) {
                        fsfi.length = file.Length;
                        CopyStream(file, gz);
                    }
                }
                fsfi.pos = lengthsum;
                var temp = (int)output.Position;
                fsfi.ziplen = temp - lengthsum;
                fsfi.lwt = ConvertDateTimeInt(x.lwt);
                files.Add(fsfi);
                lengthsum = temp;
                i++;
            }
            header.Files = files.ToArray();
            header.CreateTime = ConvertDateTimeInt(DateTime.Now);
            debugoutput?.WriteLine("[Finishing] Writing PkgInfo...");
            using (var ms = new MemoryStream(16 * 1024)) {
                using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) {
                    SerializePkgHeader(header, gz);
                }
                ms.WriteTo(output);
            }

            output.Position = beginPosition;
            output.Write(BitConverter.GetBytes(lengthsum), 0, sizeof(int));
            debugoutput?.WriteLine("Finished!");
        }

        static string getName(string rootdir, FileInfo fi)
        {
            if (string.IsNullOrEmpty(rootdir)) {
                return fi.Name;
            } else {
                if (!rootdir.EndsWith("\\") && !rootdir.EndsWith("/"))
                    rootdir += "\\";
                return MakeRelativePath(rootdir, fi.FullName);
            }
        }

        private static void SerializePkgHeader(PkgHeader header, GZipStream gz)
        {
            var bytes = Encoding.UTF8.GetBytes(SimpleJson.SerializeObject(header));
            gz.Write(bytes, 0, bytes.Length);
        }

        private PkgHeader DeserialzePkgHeader(int headerbegin)
        {
            stream.Position = headerbegin;
            PkgHeader header;
            using (var gz = new GZipStream(stream, CompressionMode.Decompress, true)) {
                var rs = new StreamReader(gz, Encoding.UTF8);
                var json = rs.ReadToEnd();
                header = SimpleJson.DeserializeObject<PkgHeader>(json);
            }
            posOffset = 0;
            return header;
        }

        public static void CopyStream(Stream from, Stream to, long size = -1, int bs = 16 * 1024)
        {
            ReadStream(from, (buf, len) => to.Write(buf, 0, len), size, bs);
        }

        public static Task CopyStreamAsync(Stream from, Stream to, long size = -1, int bs = 16 * 1024)
        {
            return ReadStreamAsync(from, (buf, len) => to.WriteAsync(buf, 0, len), size, bs);
        }

        public static void ReadStream(Stream from, Action<byte[], int> action, long size = -1, int bs = 16 * 1024)
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
                    int read = @from.Read(buffer, 0, bufferSize);
                    if (read == 0)
                        break;
                    action(buffer, read);
                }
            } else {
                while (true) {
                    int read = @from.Read(buffer, 0, (int)(size > bufferSize ? bufferSize : size));
                    if (read == 0)
                        throw new EndOfStreamException();
                    action(buffer, read);
                    size -= read;
                    if (size <= 0)
                        return;
                }
            }
        }

        public static async Task ReadStreamAsync(Stream from, Func<byte[], int, Task> action, long size = -1, int bs = 16 * 1024)
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
                    int read = await @from.ReadAsync(buffer, 0, bufferSize);
                    if (read == 0)
                        break;
                    await action(buffer, read);
                }
            } else {
                while (true) {
                    int read = await @from.ReadAsync(buffer, 0, (int)(size > bufferSize ? bufferSize : size));
                    if (read == 0)
                        throw new EndOfStreamException();
                    await action(buffer, read);
                    size -= read;
                    if (size <= 0)
                        return;
                }
            }
        }

        static int toint32(long l)
        {
            if (l > int.MaxValue)
                return int.MaxValue;
            return (int)l;
        }

        public static string MakeRelativePath(string basePath, string absPath, bool dosSeparator = true)
        {
            if (string.IsNullOrEmpty(basePath)) throw new ArgumentNullException(nameof(basePath));
            if (string.IsNullOrEmpty(absPath)) throw new ArgumentNullException(nameof(absPath));

            Uri fromUri = new Uri(basePath);
            Uri toUri = new Uri(absPath);

            Uri relativeUri = fromUri.MakeRelativeUri(toUri);
            string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            return dosSeparator ? relativePath.Replace('/', '\\') : relativePath;
        }

        byte[] readbytes(Stream stream, int size)
        {
            var bytes = new byte[size];
            int cur = 0;
            while (true) {
                var read = stream.Read(bytes, cur, size);
                cur += read;
                size -= read;
                if (size < 1)
                    return bytes;
                if (read == 0)
                    throw new EndOfStreamException();
            }
        }
        
        public static DateTime ConvertIntDateTime(double d)
        {
            DateTime startTime = TimeZone.CurrentTimeZone.ToLocalTime(new DateTime(1970, 1, 1));
            return startTime.AddMilliseconds(d);
        }
        
        public static long ConvertDateTimeInt(DateTime time)
        {
            DateTime startTime = TimeZone.CurrentTimeZone.ToLocalTime(new DateTime(1970, 1, 1, 0, 0, 0, 0));
            return (time.Ticks - startTime.Ticks) / 10000;
        }
    }

    public class AddingFSFile
    {
        public string name;
        public Func<Stream> file;
        public DateTime lwt;

        public AddingFSFile()
        {
        }

        public AddingFSFile(string filePath, string name)
        {
            this.name = name;
            file = () => File.OpenRead(filePath);
            lwt = new FileInfo(filePath).LastWriteTimeUtc;
        }
    }

    [Serializable]
    class PkgHeader
    {
        public int NZipMinVersion;
        public int NZipVersion;
        public long CreateTime;
        public NZFileinfo[] Files;
    }

    [Serializable]
    public class NZFileinfo
    {
        public string name;
        public long pos;
        public long length;
        public long ziplen;
        public long lwt;

        public DateTime GetLastWriteTime() => NZ.ConvertIntDateTime(lwt);
    }
}
