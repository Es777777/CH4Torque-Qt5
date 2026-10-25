using System;
using System.Windows.Forms;

namespace CH4Torque
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序无法继续运行：\n" + ex.Message, "CH4 力矩采集", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
