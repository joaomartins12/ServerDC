using System;
using System.Windows.Forms;

namespace ServerManager
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new MainForm();
            ManagerEnhancements.Attach(form);
            VehicleKeySettingsExtension.Attach(form);
            LogClearBehavior.Attach(form);
            Application.Run(form);
        }
    }
}
