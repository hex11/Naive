using System;
using System.Collections.Generic;
using System.Text;

namespace NaiveSocks
{
    public static class BuildInfo
    {
        public const string Version = "0.3.1.2";
        public const bool Debug =
#if DEBUG
            true;
#else
            false;
#endif

        public static string CurrentVersion => Version;
        public static bool CurrentDebug => Debug;
    }
}
