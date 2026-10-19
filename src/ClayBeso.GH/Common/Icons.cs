using System.Collections.Generic;
using System.Drawing;
using System.Reflection;

namespace ClayBeso.GH.Common
{
    /// <summary>
    /// 24x24 component icons embedded from src/ClayBeso.GH/Icons/*.png. Edit the .svg there and
    /// run scripts/build-icons.ps1 to regenerate the PNGs.
    /// </summary>
    internal static class Icons
    {
        private static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>();

        /// <summary>The icon named like its source file (e.g. "ShellModel"), or null if missing.</summary>
        public static Bitmap Get(string name)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(name, out var bmp)) return bmp;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ClayBeso.Icons.{name}.png"))
                    bmp = s == null ? null : new Bitmap(s);
                Cache[name] = bmp;
                return bmp;
            }
        }
    }
}
