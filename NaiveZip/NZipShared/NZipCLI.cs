using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using static System.Console;

namespace NZip
{
    public class NZipCLI
    {
        public static void CliMain(string[] args)
        {
            if (MagicExe.IsExeAttached) {
                MagicExe.RunAttachedExe(args);
                return;
            }

            inputstrs = args;
            while (true) {
                var action = input("Action (Create/PackDir/Ls/eXtract/PackmagicExe/Quit): ");
                try {
                    if (action == null)
                        return;
                    if (action == "c")
                        Qcreatezip();
                    else if (action == "pd")
                        Qcreatezip(true);
                    else if (action == "l")
                        Qlszip();
                    else if (action == "x")
                        Qunzip();
                    else if (action == "pe")
                        QpackMagicExe();
                    else if (action == "q")
                        return;
                } catch (Exception ex) {
                    WriteLine();
                    ForegroundColor = ConsoleColor.Red;
                    WriteLine(ex.ToString());
                    ResetColor();
                    if (args.Length > 0)
                        return;
                }
                WriteLine();
            }
        }

        private static void Qunzip()
        {
            var file = input("NZip File: ");
            var dir = input("Output Dir: ").TrimEnd('\\', '/');
            var fs = File.OpenRead(file);
            var fsz = NZ.FromStream(fs);
            WriteLine("[Files:]");
            foreach (var item in fsz.GetFiles()) {
                WriteLine(item.name);
                var outputpath = $"{dir}\\{item.name}";
                var fi = new FileInfo(outputpath);
                if (Directory.Exists(Path.GetDirectoryName(outputpath)) == false)
                    Directory.CreateDirectory(fi.DirectoryName);
                using (var ofs = File.Open(outputpath, FileMode.Create, FileAccess.ReadWrite)) {
                    ofs.SetLength(item.length);
                    fsz.WriteFileTo(item, ofs);
                }
                if (item.lwt > 0)
                    fi.LastWriteTime = item.GetLastWriteTime();
            }
            fs.Close();
        }

        private static void Qlszip()
        {
            var file = input("NZip to list: ");
            var fs = File.OpenRead(file);
            var fsz = NZ.FromStream(fs);
            if (fsz.CreateTime != DateTime.MinValue)
                WriteLine($"CreateTime: {fsz.CreateTime}");
            WriteLine("[Files:]");
            WriteLine("Size\tGZSize\tName");
            var files = fsz.GetFiles();
            long sizesum = 0;
            long ziplensum = 0;
            foreach (var item in files) {
                WriteLine($"{item.length}\t{item.ziplen}\t{item.name}");
                sizesum += item.length;
                ziplensum += item.ziplen;
            }
            WriteLine($"[Total {files.Length} files, {ziplensum}/{sizesum} bytes ({Convert.ToInt32((double)ziplensum / sizesum * 100)} %)]");
        }

        private static void Qcreatezip(bool ignoreFiles = false)
        {
            List<string> files = new List<string>();
            if (ignoreFiles == false) {
                files.AddRange(from x in inputArray("Input Files") select x.Trim('\"'));
            }
            var dir = input("Root Dir: ");
            if (dir != "") {
                dir = new DirectoryInfo(dir).FullName;
            }
            if (files.Count < 1) {
                files.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            }
            var output = input("Output File: ");
            var fs = File.Open(output, FileMode.Create, FileAccess.ReadWrite);
            NZ.Create(fs, files.ToArray(), dir, Out);
            fs.Flush();
            fs.Close();
        }

        static string[] inputstrs;
        static int currentinputstr = 0;

        static string input(string str)
        {
            ForegroundColor = ConsoleColor.Green;
            Write(str);
            ResetColor();
            if (inputstrs != null && inputstrs.Length > 0) {
                if (currentinputstr >= inputstrs.Length) {
                    WriteLine("[EOF]");
                    //throw new Exception("EOF");
                    return null;
                }
                var ret = inputstrs[currentinputstr];
                currentinputstr++;
                WriteLine(ret);
                return ret;
            }
            return ReadLine();
        }

        static List<string> inputArray(string str)
        {
            var arr = new List<string>();
            while (true) {
                var line = input($"{str}[{arr.Count}]: ");
                if (line == null)
                    throw new Exception("EOF");
                if (line?.Length == 0)
                    return arr;
                arr.Add(line);
            }
        }

        private static void QpackMagicExe()
        {
            var exe = input("EXE file: ");
            var dlls = inputArray("Referenced files/dirs");
            var outputPath = input("Output file: ");
            MagicExe.AttachExe(exe, dlls, outputPath);
        }
    }
}
