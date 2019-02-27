using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace NZip
{
    public static class MagicExe
    {
        public static bool IsExeAttached => Magic.magic_char == 'm';
        public static bool IsDllsAttached => Magic.magic_char == 'd';

        const string NamePrefix = "bin\\";
        const string ExeName = NamePrefix + "<>main.exe";

        public static void AttachExe(string exe, List<string> dlls, string outputPath)
        {
            PackCore(exe, dlls, outputPath);
        }

        public static void AttachDlls(List<string> dlls, string outputPath)
        {
            PackCore(null, dlls, outputPath);
        }

        static void PackCore(string exe, List<string> dlls, string outputPath)
        {
            using (var fs = File.Open(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None)) {
                bool extExe = exe != null;
                var list = new List<AddingFile>();
                bool isGui = false;
                byte[] exebuf;
                if (extExe) {
                    exebuf = File.ReadAllBytes(exe);
                    isGui = Magic.getSubsystem(exebuf) == 2;
                    list.Add(new AddingFile(exe, ExeName));
                }
                int exeLength = GetExeLength();
                var selfexebuf = Magic.genExe(extExe ? 'm' : 'd', isGui, exeLength.ToString(), exeLength);
                fs.Write(selfexebuf, 0, selfexebuf.Length);
                int i = 0;
                if (dlls != null) {
                    foreach (var x in dlls) {
                        i++;
                        if (File.Exists(x)) {
                            var name = AssemblyName.GetAssemblyName(x).FullName;
                            list.Add(new AddingFile(x, NamePrefix + name));
                        } else if (Directory.Exists(x)) {
                            // TODO
                            throw new NotImplementedException();
                        }
                    }
                }
                NZ.Create(fs, list, Console.Out);
            }
        }


        public static void LoadAttachedDlls()
        {
            if (!IsDllsAttached) throw new Exception("DLLs are not attached.");
            NZ fsz = GetPack();
            RegisterAssemblyResolver(fsz);
        }

        public static void RunAttachedExe(string[] args)
        {
            if (!IsExeAttached) throw new Exception("exe is not attached.");

            NZ fsz = GetPack();
            RegisterAssemblyResolver(fsz);

            var exeBytes = fsz.GetFileBytes(ExeName);
            var exeAssembly = Assembly.Load(exeBytes);
            var entryPoint = exeAssembly.EntryPoint;

            var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var isPackedField = entryPoint.DeclaringType.GetField("__magic_is_packed", flags);
            var pkgField = entryPoint.DeclaringType.GetField("__magic_files", flags);
            if (isPackedField != null && isPackedField.FieldType == typeof(bool)) {
                isPackedField.SetValue(null, true);
            }
            if (pkgField != null && pkgField.FieldType == typeof(IDictionary<string, Func<byte[]>>)) {
                var fszDict = new Dictionary<string, Func<byte[]>>();
                foreach (var item in fsz.GetFiles()) {
                    fszDict.Add(item.name, () => fsz.GetFileBytes(item));
                }
                pkgField.SetValue(null, fszDict);
            }

            entryPoint.Invoke(null, new object[] { args });
        }

        private static void RegisterAssemblyResolver(NZ fsz)
        {
            var asms = new Dictionary<string, NZFileinfo>();
            foreach (var item in fsz.GetFiles()) {
                if (item.name != ExeName && item.name.StartsWith(NamePrefix)) {
                    asms.Add(item.name.Substring(NamePrefix.Length), item);
                }
            }
            AppDomain.CurrentDomain.AssemblyResolve += (s, arg) => {
                if (!asms.TryGetValue(arg.Name, out var nfi)) {
                    //Console.Error.WriteLine($"NZip: assembly '{arg.Name}' not found");
                    return null;
                }
                return Assembly.Load(fsz.GetFileBytes(nfi));
            };
        }

        public static NZ GetPack()
        {
            var offset = GetPackOffset();
            var fsz = NZ.FromStream(File.Open(Magic.GetSelfPath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite), offset);
            return fsz;
        }

        private static int GetPackOffset()
        {
            return int.Parse(Magic.getMagicB());
        }

        private static int GetExeLength()
        {
            if (IsExeAttached || IsDllsAttached) {
                return GetPackOffset();
            } else {
                return (int)new FileInfo(Magic.GetSelfPath()).Length;
            }
        }
    }
}
