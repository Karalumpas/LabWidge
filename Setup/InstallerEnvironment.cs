using System;
using System.IO;

namespace LabWidgeSetup
{
    internal static class InstallerEnvironment
    {
        /// <summary>Windows locks working directories. Setup must not hold open the folder that is about to be switched.</summary>
        public static void Prepare()
        {
            Environment.CurrentDirectory = Path.GetTempPath();
        }
    }
}
