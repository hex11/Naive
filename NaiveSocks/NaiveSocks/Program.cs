using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Naive;

namespace NaiveSocks
{
    class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (NZip.MagicExe.IsDllsAttached) {
                NZip.MagicExe.LoadAttachedDlls();
                NaiveSocksCli.SingleFile = true;
            }
            if (args.Length > 1 && args[0] == "--repack") {
                Repack(args);
                return;
            }
            var dict = NZip.Magic.getMagicDict();
            if (dict.TryGetValue("gui", out var val) && val == "1") {
                NaiveSocksCli.GuiMode = true;
            }
            NaiveSocksCli.Main(args);
        }

        private static void Repack(string[] args)
        {
            var argparser = new ArgumentParser();
            argparser.AddArg(ParasPara.OnePara, "-o", "--output");
            argparser.AddArg(ParasPara.OneOrMoreParas, "--dlls");
            argparser.AddArg(ParasPara.NoPara, "--gui");
            argparser.AddArg(ParasPara.NoPara, "--no-gui");
            var r = argparser.ParseArgs(args);
            var output = r["-o"].FirstParaOrThrow;
            var dlls = r.GetOrNull("--dlls")?.paras;
            bool? setGui = null;
            var dict = new NZip.Magic.Dict();
            if (r.ContainsKey("--gui")) {
                setGui = true;
                dict["gui"] = "1";
            } else if (r.ContainsKey("--no-gui")) {
                setGui = false;
                dict["gui"] = null;
            }
            NZip.MagicExe.Pack(null, dlls, output, dict, setGui);
        }
    }
}
