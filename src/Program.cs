// LUM - point d'entrée.
using System;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("LUM - bulle caméra")]
[assembly: System.Reflection.AssemblyProduct("LUM")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace Lum
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "LUM_camera_bubble_single_instance", out created))
            {
                if (!created) return; // déjà lancé
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new BubbleForm(Settings.Load()));
            }
        }
    }
}
