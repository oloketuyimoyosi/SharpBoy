using System.Diagnostics;

namespace GameboyTest
{

    public class Timer
    {
        // The 4 Hardware Registers

        public byte DIV { get => io[0x4]; set => io[0x4] = value; } // 0xFF04
        public byte TIMA { get => io[0x5]; set => io[0x5] = value; }        // 0xFF05
        public byte TMA { get => io[0x6]; set => io[0x6] = value; }         // 0xFF06
        public byte TAC { get => io[0x7]; set => io[0x7] = value; }         // 0xFF07
        private bool intr =true;
        // --- 2. INTERNAL STATE & TIMING ---
        private int divCounter = 0;
        private int timaCounter = 0;
        public byte[] io { get; set; }
        // The raw T-Cycle thresholds for the 4 frequencies: 4096 Hz, 262144 Hz, 65536 Hz, 16384 Hz
        private readonly int[] FrequencyThresholds = { 1024/4, 16/4, 64/4, 256 / 4 };

        // Cached threshold based on your optimization!
        private int currentThreshold = 1024/4;

        // Callback to tell the MemoryBus to flip the IF interrupt flag
        private Action requestInterrupt;

        public Timer(Action interruptCallback, byte[] io)
        {
            this.requestInterrupt = interruptCallback;
            this.io = io;
        }
        public void Tick(int cycles)
        {
            // 1. DIV always runs, completely independent of TAC
            divCounter += cycles;
            if (divCounter >= 255)
            {
                divCounter =0;
                DIV++; // C# automatically wraps 255 to 0 because it's a byte
            } // For debugging, since we don't have the full interrupt system implemented yet
            // 2. Check if TIMA is enabled (Bit 2 of TAC is 1)
            if ((TAC & 0x04) != 0)
            {
                timaCounter += cycles/4;
                currentThreshold = FrequencyThresholds[TAC & 0x3];
                while (timaCounter >= currentThreshold)
                {
                    timaCounter -= currentThreshold;
                    
                    currentThreshold = FrequencyThresholds[TAC&0x3];
                    if (TIMA == 255) // Overflow!
                    {
                        
                        TIMA = TMA; // Reload with Modulo
 
                        requestInterrupt();

                        
                        TIMA = TMA;
                        if (DIV == 0)
                        {
                            io[0xF] &= 0xFB;
                        }// Trigger the Timer Interrupt!
                    }
                    else
                    {
                        TIMA+=1;
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

