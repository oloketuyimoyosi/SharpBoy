using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameboyTest.Debugger
{
    using System;
    using System.IO;
    using System.Diagnostics;

    public class CpuLogger
    {
        private StreamWriter writer;
        private bool isLogging = false;

        public void Start(string filePath)
        {
            try
            {
                // If the file is locked by a previous crash, this forces a clean start
                if (writer != null) { Stop(); }

                // FileMode.Create: Overwrites if exists
                // FileShare.ReadWrite: Prevents the "Process cannot access file" error
                FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

                writer = new StreamWriter(fs);
                writer.AutoFlush = true; // This is the most important line
                isLogging = true;

                Debug.WriteLine($"LOGGER: Started at {filePath}");
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("LOGGER START ERROR: " + ex.Message);
            }
        }

        public void LogPC(ushort pc)
        {
            // Don't use a StreamWriter. Just append directly to the file and close it immediately.
            // This way, if the CPU crashes at 0x0120, the line for 0x011F is already safe on the disk.
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cpu_trace.txt");
                File.AppendAllText(logPath, $"PC: {pc:X4}{Environment.NewLine}");
            }
            catch { /* Ignore lock errors */ }
        }
        public void Stop()
        {
            isLogging = false;
            if (writer != null)
            {
                writer.Flush();
                writer.Close();
                writer.Dispose();
                writer = null;
                Debug.WriteLine("LOGGER: Stopped and file saved.");
            }
        }
    }
}
