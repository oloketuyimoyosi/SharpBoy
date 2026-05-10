namespace GameboyTest.Debugger
{
    using System;
    using System.Diagnostics;
    using System.IO;

    public class CpuLogger
    {
        private StreamWriter writer;
        private bool isLogging = false;

        // Blargg validation properties
        private string[] blarggTrace;
        private int currentLineIndex = 0;
        private bool isStrictValidation = false;

        public void Start(string filePath, string blarggValidationPath = null)
        {
            try
            {
                // If the file is locked by a previous crash, this forces a clean start
                if (writer != null) { Stop(); }

                // Ensure directory exists
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // FileMode.Create: Overwrites if exists
                // FileShare.ReadWrite: Prevents the "Process cannot access file" error
                FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

                writer = new StreamWriter(fs);
                writer.AutoFlush = true; // This is the most important line
                isLogging = true;

                if (!string.IsNullOrEmpty(blarggValidationPath))
                {
                    if (File.Exists(blarggValidationPath))
                    {
                        blarggTrace = File.ReadAllLines(blarggValidationPath);
                        currentLineIndex = 0;
                        isStrictValidation = true;
                        Debug.WriteLine($"LOGGER: Strict validation enabled with {blarggValidationPath}");
                    }
                    else
                    {
                        System.Windows.Forms.MessageBox.Show($"Blargg trace file not found at:\n{blarggValidationPath}\n\nPlease make sure to copy your blargg test file here, or update the path in Form1.cs", "Validation File Missing", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
                    }
                }

                Debug.WriteLine($"LOGGER: Started at {filePath}");
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("LOGGER START ERROR: " + ex.Message);
            }
        }

        public void LogState(CPU cpu, MemoryBus bus)
        {
            if (!isLogging || writer == null) return;

            // Matches Blargg trace format perfectly:
            // "A: 01 F: B0 B: 00 C: 13 D: 00 E: D8 H: 01 L: 4D SP: FFFE PC: 00:0100"
            string logEntry = $"A: {cpu.A:X2} F: {cpu.F:X2} B: {cpu.B:X2} C: {cpu.C:X2} D: {cpu.D:X2} E: {cpu.E:X2} H: {cpu.H:X2} L: {cpu.L:X2} SP: {cpu.SP:X4} PC: 00:{cpu.PC:X4}";

            if (isStrictValidation && blarggTrace != null && currentLineIndex < blarggTrace.Length)
            {
                string expectedLine = blarggTrace[currentLineIndex];

                // Cut off anything coming after the " (" which is where the memory peek starts
                string expectedBase = expectedLine;
                int peekIndex = expectedLine.IndexOf(" (");
                if (peekIndex > 0)
                {
                    expectedBase = expectedLine.Substring(0, peekIndex).Trim();
                }

                // Compare only the cleaned string
                if (!expectedBase.Equals(logEntry, StringComparison.OrdinalIgnoreCase))
                {
                    Stop();
                    // Show a MessageBox so it pops up cleanly over the emulator, 
                    // since background task exceptions are often silently swallowed or truncated.
                    string errorMsg = $"--- DESYNC DETECTED ---\nStep: {currentLineIndex}\n\nExpected:\n{expectedBase}\n\nActual (Your Emulator):\n{logEntry}";
                    System.Windows.Forms.MessageBox.Show(errorMsg, "CPU Validation Failed", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                    throw new Exception(errorMsg);
                }
                currentLineIndex++;
                logEntry = $"A: {cpu.A:X2} F: {cpu.F:X2} B: {cpu.B:X2} C: {cpu.C:X2} D: {cpu.D:X2} E: {cpu.E:X2} H: {cpu.H:X2} L: {cpu.L:X2} SP: {cpu.SP:X4} PC: 00:{cpu.PC:X4} {bus.ppu.LY} {bus.ReadByte(cpu.PC)}";
            }

            try
            {
                // Write directly to the open stream rather than opening/closing the file on every tick
                writer.WriteLine(logEntry);
            }
            catch { /* Ignore lock errors */ }
        }

        public void LogPC(ushort pc, MemoryBus bus)
        {
            if (!isLogging || writer == null) return;

            string logEntry = $"PC: {pc:X4}";

            if (isStrictValidation && blarggTrace != null && currentLineIndex < blarggTrace.Length)
            {
                string expectedLine = blarggTrace[currentLineIndex];
                if (expectedLine != logEntry)
                {
                    Stop();
                    throw new Exception($"DESYNC DETECTED at CPU step {currentLineIndex}!\nExpected: {expectedLine}\nActual:   {logEntry}");
                }
                currentLineIndex++;
            }

            try
            {
                // Write directly to the open stream rather than opening/closing the file on every tick
                writer.WriteLine(logEntry + $"{bus.io[0X44]}");
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
