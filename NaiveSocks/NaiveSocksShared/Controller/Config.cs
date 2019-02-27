using Naive.HttpSvr;
using Nett;
using System;
using System.Collections.Generic;
using System.Text;

namespace NaiveSocks
{
    public class Config
    {
        public string socket_impl { get; set; }
        public Logging.Level log_level { get; set; } =
#if DEBUG
            Logging.Level.None;
#else
            Logging.Level.Info;
#endif

        public string dir { get; set; }

        public DebugSection debug { get; set; }

        public Dictionary<string, string> aliases { get; set; }

        public Dictionary<string, TomlTable> @in { get; set; }
        public Dictionary<string, TomlTable> @out { get; set; }

        public class DebugSection
        {
            public string[] flags { get; set; }
        }
    }

    public class LoadedConfig
    {
        public List<InAdapter> InAdapters = new List<InAdapter>();
        public List<OutAdapter> OutAdapters = new List<OutAdapter>();

        public Dictionary<string, string> Aliases = new Dictionary<string, string>();

        public string[] DebugFlags;

        public string SocketImpl;

        public Logging.Level LoggingLevel;

        public string FilePath;
        public string WorkingDirectory = ".";

        public int FailedCount;

        public TomlTable TomlTable;
    }
}
