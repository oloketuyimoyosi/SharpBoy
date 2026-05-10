namespace GameboyTest.MBC
{
    public class Mbc1 : IMbc
    {
        private byte[][] romBanks;
        private byte[][] ramBanks;

        // MBC1 splits the ROM bank selection into two separate registers
        private int romBankLower = 1; // 5 bits
        private int romBankUpper = 0; // 2 bits

        private bool ramEnabled = false;

        // 0 = ROM Banking Mode (Up to 2MB ROM, 8KB RAM)
        // 1 = RAM Banking Mode (Up to 512KB ROM, 32KB RAM)
        private int bankingMode = 0;

        public Mbc1(byte[][] romBanks, byte[][] ramBanks)
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
                int currentRomBank = GetCalculatedRomBank();
                int offset = address - 0x4000;

                return romBanks[currentRomBank][offset];
            }
            // 3. External Save RAM (0xA000 - 0xBFFF)
            else if (address >= 0xA000 && address <= 0xBFFF)
            {
                if (ramEnabled && ramBanks != null && ramBanks.Length > 0)
                {
                    int currentRamBank = (bankingMode == 1) ? romBankUpper : 0;

                    // Safety check to ensure we don't read out of bounds
                    if (currentRamBank < ramBanks.Length)
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
            // 1. Enable/Disable RAM
            if (address <= 0x1FFF)
            {
                ramEnabled = ((value & 0x0F) == 0x0A);
            }
            // 2. ROM Bank Select (Lower 5 bits)
            else if (address >= 0x2000 && address <= 0x3FFF)
            {
                romBankLower = value & 0x1F; // Mask to 5 bits

                // MBC1 translates Bank 0 to Bank 1
                if (romBankLower == 0) romBankLower = 1;
            }
            // 3. RAM Bank Select / ROM Bank Upper Select (2 bits)
            else if (address >= 0x4000 && address <= 0x5FFF)
            {
                romBankUpper = value & 0x03; // Mask to 2 bits
            }
            // 4. ROM/RAM Mode Select
            else if (address >= 0x6000 && address <= 0x7FFF)
            {
                bankingMode = value & 0x01; // Mask to 1 bit
            }
            // 5. Write to External RAM
            else if (address >= 0xA000 && address <= 0xBFFF)
            {
                if (ramEnabled && ramBanks != null && ramBanks.Length > 0)
                {
                    int currentRamBank = (bankingMode == 1) ? romBankUpper : 0;

                    if (currentRamBank < ramBanks.Length)
                    {
                        int offset = address - 0xA000;
                        ramBanks[currentRamBank][offset] = value;
                    }
                }
            }
        }

        // --- Helper Method to handle MBC1 Quirks ---

        private int GetCalculatedRomBank()
        {
            // Combine the upper and lower bits to get the full 7-bit bank number
            int calculatedBank = (romBankUpper << 5) | romBankLower;

            // THE MBC1 QUIRK: It cannot address banks 0x20, 0x40, or 0x60.
            // It automatically increments them by 1.
            if (calculatedBank == 0x20 || calculatedBank == 0x40 || calculatedBank == 0x60)
            {
                calculatedBank++;
            }

            // Safety wrapper: If a bad ROM asks for a bank outside its actual file size, 
            // wrap it back around to prevent the emulator from crashing.
            return calculatedBank % romBanks.Length;
        }
    }
}
