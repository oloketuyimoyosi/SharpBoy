using System.Diagnostics;

namespace GameboyTest
{

    public class Timer
    {
        // The 4 Hardware Registers
        private bool reloadTima;
        private bool reloadingTima;
        private bool lastResult;
        private int divBit;

        private byte tima;
        private byte tma;
        private byte tac;
        public byte DIV { get => (byte)(divCounter>>8) ; set
            {
                divCounter = 0;
                if (!lastResult) {
                    return;
                }
                DetectFallingEdge();

            } 

            } // 0xFF04
        public byte TIMA { get => tima; set { 
            if (reloadingTima)
                {
                    return;
                }
            tima = value;
            reloadingTima = false;
            
            
            } }        // 0xFF05
        public byte TMA { get => tma; set { 
                tma = value;
                if (reloadingTima) { 
                    tima = tma;
                }


            } }         // 0xFF06
        public byte TAC { get=> tac; set {
                tac = value;
                divBit = FrequencyBits[tac & 0x3];
                if (!lastResult)
                {
                    return;
                }
                DetectFallingEdge();
            } }         // 0xFF07
        private bool intr =true;
        // --- 2. INTERNAL STATE & TIMING ---
        private int divCounter = 0;
        private int timaCounter = 0;
        

        // The raw T-Cycle thresholds for the 4 frequencies: 4096 Hz, 262144 Hz, 65536 Hz, 16384 Hz
        private readonly int[] FrequencyThresholds = { 1024, 16, 64, 256};

        // Cached threshold based on your optimization!
        private int currentThreshold = 1024;
        private int[] FrequencyBits = { 9, 3, 5, 7 };
        
        // Callback to tell the MemoryBus to flip the IF interrupt flag
        private Action requestInterrupt;

        public Timer(Action interruptCallback)
        {
            this.requestInterrupt = interruptCallback;

        }
        public void Tick(int cycles)
        {
            // 1. DIV always runs, completely independent of TAC
            /*divCounter += cycles;
            if (divCounter >= 256)
            {
                divCounter -= 256;
                DIV++; // C# automatically wraps 255 to 0 because it's a byte
            }

            // 2. Check if TIMA is enabled (Bit 2 of TAC is 1)
            if ((TAC & 0x04) != 0)
            {
                timaCounter += cycles;
                currentThreshold = FrequencyThresholds[TAC & 0x3];
                // Use the while loop to catch up on long CPU instructions!

                if (timaCounter > currentThreshold)
                {
                    timaCounter -= currentThreshold;
                    currentThreshold = FrequencyThresholds[TAC & 0x3];
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
            }*/

            reloadingTima = false;
            if (reloadTima)
            {
                reloadTima = false;
                reloadingTima = true;
                tima = tma;
                requestInterrupt();
            }
            divCounter += cycles;
            DetectFallingEdge();
        }


        // --- 4. HARDWARE INTERCEPTS ---

        // Called when MemoryBus writes to 0xFF07


        // Called when MemoryBus writes ANY value to 0xFF04
        public void ResetDiv()
        {
            DIV = 0;
            divCounter = 0;
        }
        private void DetectFallingEdge()
        {
            bool result = ((tac&0x4)!=0)&&((divCounter&(1<<divBit))!=0);
            if (lastResult && !result)
            {
                tima++;
                if (tima == 0)
                {
                    reloadTima = true;
                }
            }
            lastResult = result;
        }
    }
}

