namespace GameboyTest.MBC
{
    public class Mbc3 : IMbc
    {
        private byte[][] romBanks;
        private byte[][] ramBanks;

        // MBC3 State Variables
        private int currentRomBank = 1;
        private int currentRamBank = 0;
        private bool ramAndRtcEnabled = false;

        // --- NEW: RTC State Variables ---
        // -1 means no RTC selected (we are reading/writing RAM). 
        // 0-4 corresponds to the 5 clock registers.
        private int activeRtcRegister = -1;

        // An array to hold our 5 clock values (S, M, H, DL, DH)
        private byte[] rtcData = new byte[5];

        public Mbc3(byte[][] romBanks, byte[][] ramBanks)
        {
            this.romBanks = romBanks;
            this.ramBanks = ramBanks;
        }

        public byte Read(ushort address)
        {
            // 1. Fixed ROM Bank 0 (0x0000 - 0x3FFF)
            if (address <= 0x3FFF)
            {
                return romBanks[0][address];
            }
            // 2. Switchable ROM Bank (0x4000 - 0x7FFF)
            else if (address >= 0x4000 && address <= 0x7FFF)
            {
                int offset = address - 0x4000;
                if (currentRomBank < romBanks.Length)
                {
                    return romBanks[currentRomBank][offset];
                }
            }
            // 3. External RAM or RTC Data (0xA000 - 0xBFFF)
            else if (address >= 0xA000 && address <= 0xBFFF)
            {
                if (ramAndRtcEnabled)
                {
                    // If an RTC register is actively selected, return the clock data
                    if (activeRtcRegister != -1)
                    {
                        return rtcData[activeRtcRegister];
                    }
                    // Otherwise, return standard Save RAM data
                    else if (ramBanks != null && currentRamBank < ramBanks.Length)
                    {
                        int offset = address - 0xA000;
                        return ramBanks[currentRamBank][offset];
                    }
                }
            }

            return 0xFF;
        }

        public void Write(ushort address, byte value)
        {
            // 1. Enable/Disable RAM and RTC
            if (address <= 0x1FFF)
            {
                ramAndRtcEnabled = ((value & 0x0F) == 0x0A);
            }
            // 2. ROM Bank Select
            else if (address >= 0x2000 && address <= 0x3FFF)
            {
                currentRomBank = value & 0x7F;
                if (currentRomBank == 0) currentRomBank = 1;
            }
            // 3. RAM Bank OR RTC Register Select
            else if (address >= 0x4000 && address <= 0x5FFF)
            {
                // Values 0x00-0x07 map to physical RAM banks
                if (value <= 0x07)
                {
                    currentRamBank = value;
                    activeRtcRegister = -1; // Deselect RTC, switch back to RAM
                }
                // Values 0x08-0x0C map to the RTC registers
                else if (value >= 0x08 && value <= 0x0C)
                {
                    // Normalize 0x08-0x0C down to an array index of 0-4
                    activeRtcRegister = value - 0x08;
                }
            }
            // 4. Latch Clock Data (0x6000 - 0x7FFF)
            else if (address >= 0x6000 && address <= 0x7FFF)
            {
                // To prevent the clock from ticking forward WHILE a game is trying to read it,
                // games write 0x00 then 0x01 to this address to "freeze" the time into the registers.
                // (You can implement the actual PC system clock hooking here later!)
            }
            // 5. Write to External RAM or RTC Data (0xA000 - 0xBFFF)
            else if (address >= 0xA000 && address <= 0xBFFF)
            {
                if (ramAndRtcEnabled)
                {
                    if (activeRtcRegister != -1)
                    {
                        rtcData[activeRtcRegister] = value;
                    }
                    else if (ramBanks != null && currentRamBank < ramBanks.Length)
                    {
                        int offset = address - 0xA000;
                        ramBanks[currentRamBank][offset] = value;
                    }
                }
            }
        }
    }
}