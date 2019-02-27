using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NaiveSocks
{
    class Program
    {
        private static void Main(string[] args)
        {
            if (NZip.MagicExe.IsDllsAttached) {
                NZip.MagicExe.LoadAttachedDlls();
                NaiveSocksCli.__magic_is_packed = true;
            } else if (args.Length > 1 && args[0] == "--attach-dlls") {
                AttachDlls(args);
                return;
            }
            NaiveSocksCli.Main(args);
        }

        private static void AttachDlls(string[] args)
        {
            var output = args[1];
            var dlls = args.Skip(2).ToList();
            NZip.MagicExe.AttachDlls(dlls, output);
        }
    }
}
