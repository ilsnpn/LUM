// LUM - réglages mémorisés dans %APPDATA%\LUM\settings.ini (taille, position, miroir, caméra).
using System;
using System.Collections.Generic;
using System.IO;

namespace Lum
{
    public sealed class Settings
    {
        public int SizeIndex = 1;
        public bool Mirror = true;
        public int CameraIndex = 0;
        public int MicDevice = -1;      // -1 = micro par défaut, -2 = pas de son, >=0 = micro n°
        public bool RecordFlip;         // à activer (recordflip=1) si une vidéo sort à l'envers
        public bool HasPosition;
        public int X, Y;

        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LUM", "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                int v;
                if (d.ContainsKey("size") && int.TryParse(d["size"], out v)) s.SizeIndex = v;
                if (d.ContainsKey("camera") && int.TryParse(d["camera"], out v)) s.CameraIndex = v;
                if (d.ContainsKey("mic") && int.TryParse(d["mic"], out v)) s.MicDevice = v;
                if (d.ContainsKey("recordflip")) s.RecordFlip = d["recordflip"] == "1";
                if (d.ContainsKey("mirror")) s.Mirror = d["mirror"] == "1";
                int x, y;
                if (d.ContainsKey("x") && d.ContainsKey("y") && int.TryParse(d["x"], out x) && int.TryParse(d["y"], out y))
                { s.X = x; s.Y = y; s.HasPosition = true; }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[] {
                    "size=" + SizeIndex, "camera=" + CameraIndex, "mirror=" + (Mirror ? "1" : "0"),
                    "mic=" + MicDevice, "recordflip=" + (RecordFlip ? "1" : "0"),
                    "x=" + X, "y=" + Y });
            }
            catch { }
        }
    }
}
