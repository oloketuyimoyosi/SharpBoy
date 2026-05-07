namespace GameboyTest.MBC
{
    public class Mbc3 : IMbc
    {
        private byte[][] romBanks;
        private byte[][] ramBanks;

        private int currentRomBank = 1;
        private int currentRamBank = 0;
        private bool ramEnabled = false;

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
            // 3. External Save RAM (0xA000 - 0xBFFF)
            else if (address >= 0xA000 && address <= 0xBFFF)
            {
                if (ramEnabled && ramBanks != null && currentRamBank < ramBanks.Length)
                {
                    int offset = address - 0xA000;
                    return ramBanks[currentRamBank][offset];
                }
            }

            return 0xFF;
        }

        public void Write(ushort address, byte value)
        {
            // 1. Enable/Disable RAM
            if (address <= 0x1FFF)
            {
                ramEnabled = (value & 0x0F) == 0x0A;
            }
            // 2. ROM Bank Select
            else if (address >= 0x2000 && address <= 0x3FFF)
            {
                currentRomBank = value & 0x7F;

                // Writing 0 still selects Bank 1 in MBC3
                if (currentRomBank == 0) currentRomBank = 1;
            }
            // 3. RAM Bank Select
            else if (address >= 0x4000 && address <= 0x5FFF)
            {
                // Values 0x00-0x03 select a RAM bank. 
                // (Values 0x08-0x0C would select RTC registers, which you can add later)
                if (value <= 0x03)
                {
                    currentRamBank = value;
                }
            }
        }
    }
}