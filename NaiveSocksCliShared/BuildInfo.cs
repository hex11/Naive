using System;
using System.Collections.Generic;
using System.Text;

namespace NaiveSocks
{
    public static class BuildInfo
    {
        static BuildInfo()
        {
            if (BuildText.StartsWith("_")) {
                BuildText = null;
            }
        }

        public const string Version = "0.3.4.0";
        public const bool Debug =
#if DEBUG
            true;
#else
            false;
#endif

        public static string BuildText = "_BUILDTEXT_";

        public static string CurrentVersion => Version;
        public static string CurrentBuildText => BuildText;
        public static bool CurrentDebug => Debug;
    }
}
