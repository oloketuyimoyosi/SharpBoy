using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameboyTest
{

    public class Timer
    {
        // The 4 Hardware Registers
        public byte DIV { get; private set; } // 0xFF04
        public byte TIMA { get; set; }        // 0xFF05
        public byte TMA { get; set; }         // 0xFF06
        public byte TAC { get; set; }         // 0xFF07

        // --- 2. INTERNAL STATE & TIMING ---
        private int divCounter = 0;
        private int timaCounter = 0;

        // The raw T-Cycle thresholds for the 4 frequencies: 4096 Hz, 262144 Hz, 65536 Hz, 16384 Hz
        private readonly int[] FrequencyThresholds = { 1024, 16, 64, 256 };

        // Cached threshold based on your optimization!
        private int currentThreshold = 1024;

        // Callback to tell the MemoryBus to flip the IF interrupt flag
        private Action requestInterrupt;

        public Timer(Action interruptCallback)
        {
            this.requestInterrupt = interruptCallback;
        }
        public void Tick(int cycles)
        {
            // 1. DIV always runs, completely independent of TAC
            divCounter += cycles;
            if (divCounter >= 256)
            {
                divCounter -= 256;
                DIV++; // C# automatically wraps 255 to 0 because it's a byte
            }

            // 2. Check if TIMA is enabled (Bit 2 of TAC is 1)
            if ((TAC & 0x04) != 0)
            {
                timaCounter += cycles;

                // Use the while loop to catch up on long CPU instructions!
                while (timaCounter >= currentThreshold)
                {
                    timaCounter -= currentThreshold;

                    if (TIMA == 255) // Overflow!
                    {
                        TIMA = TMA; // Reload with Modulo
                        requestInterrupt(); // Trigger the Timer Interrupt!
                    }
                    else
                    {
                        TIMA++;
                    }
                }
            }
        }

        // --- 4. HARDWARE INTERCEPTS ---

        // Called when MemoryBus writes to 0xFF07
        public void SetTAC(byte value)
        {
            int oldFreq = TAC & 0x03;
            TAC = value;
            int newFreq = TAC & 0x03;

            // Only update the threshold if the frequency bits actually changed
            if (oldFreq != newFreq)
            {
                currentThreshold = FrequencyThresholds[newFreq];
            }
        }

        // Called when MemoryBus writes ANY value to 0xFF04
        public void ResetDiv()
        {
            DIV = 0;
            divCounter = 0;
        }
    }
}

