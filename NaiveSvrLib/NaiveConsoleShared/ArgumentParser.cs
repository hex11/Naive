using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Naive
{
    public class ArgumentParser
    {
        private List<ArgumentDefine> argdefines = new List<ArgumentDefine>();

        public void AddArg(string arg)
        {
            argdefines.Add(new ArgumentDefine() { keys = new[] { arg }, paraspara = ParasPara.NoPara });
        }

        public void AddArg(ParasPara para, params string[] keys)
        {
            argdefines.Add(new ArgumentDefine() { keys = keys, paraspara = para });
        }

        public ArgParseResult ParseArgs(string[] args)
        {
            var dict = new ArgParseResult();
            var arglen = args.Length;
            Argument lastarg = null;
            ArgumentDefine lastdef = null;
            for (int i = 0; i < arglen; i++) {
                var arg = args[i];
                if (arg[0] == '-' && lastdef?.paraspara != ParasPara.AllParaAfterIt) {
                    bool found = false;
                    foreach (var item in argdefines) {
                        if (item.keys.Contains(arg)) {
                            dict.Add(arg = item.keys[0],
                                lastarg = new Argument() {
                                    position = i,
                                    arg = item.keys[0],
                                    paras = new List<string>()
                                });
                            lastdef = item;
                            found = true;
                            break;
                        }
                    }
                    if (found == false) {
                        dict.Add(arg,
                            lastarg = new Argument() {
                                position = i,
                                arg = arg,
                                paras = new List<string>()
                            });
                        lastdef = null;
                    }
                } else {
                    if (lastarg != null) {
                        if (lastdef != null) {
                            if (lastdef.paraspara == ParasPara.NoPara) {
                                continue; // unexpected
                            } else if (lastdef.paraspara == ParasPara.OnePara && lastarg.paras.Count > 0) {
                                continue; // unexpected
                            }
                        }
                        lastarg.paras.Add(arg);
                    } else {
                        // unexpected
                    }
                }
            }
            return dict;
        }
    }

    public class ArgParseResult : Dictionary<string, Argument>
    {
        public void IfContains(string key, Action action)
        {
            if (this.ContainsKey(key)) {
                action();
            }
        }

        public void ForEachIfContains(Dictionary<string, Action> dict)
        {
            foreach (var item in dict) {
                IfContains(item.Key, item.Value);
            }
        }

        public void TryGetValue(string key, Action<Argument> action)
        {
            Argument a;
            if (this.TryGetValue(key, out a)) {
                action(a);
            }
        }

        public void ForEachTryGetValue(Dictionary<string, Action<Argument>> dict)
        {
            foreach (var item in dict) {
                TryGetValue(item.Key, item.Value);
            }
        }

        public Argument GetOrNull(string key)
        {
            if (TryGetValue(key, out var val)) {
                return val;
            }
            return null;
        }
    }

    internal class ArgumentDefine
    {
        public string[] keys;
        public ParasPara paraspara;
    }

    public class Argument
    {
        public int position;
        public string arg;
        public List<string> paras;
        public bool HavePara => paras.Count > 0;
        public string FirstPara => paras.Count > 0 ? paras[0] : null;
        public string FirstParaOrThrow => paras.Count > 0 ? paras[0] : throw new CmdArgException($"missing first parameter for '{arg}'");
    }

    public class CmdArgException : Exception
    {
        public CmdArgException() { }

        public CmdArgException(string msg) : base(msg) { }
    }

    public enum ParasPara
    {
        NoPara,
        OnePara,
        OneOrMoreParas,
        AllParaAfterIt
    }
}
