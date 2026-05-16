using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameboyTest.MBC
{
    public class Mbc5 : IMbc
    {
        // Using the jagged arrays just like in the Mbc3 class
        private byte[][] romBanks;
        private byte[][] ramBanks;

        // MBC5 State Variables
        private int currentRomBank = 1;
        private int currentRamBank = 0;
        private bool ramEnabled = false;

        public Mbc5(byte[][] romBanks, byte[][] ramBanks)
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

                // THE FIX: Use Modulo to wrap the bank around if it exceeds the array bounds!
                int mappedBank = currentRomBank % romBanks.Length;
                return romBanks[mappedBank][offset];
            }
            // 3. External RAM (0xA000 - 0xBFFF)
            else if (address >= 0xA000 && address <= 0xBFFF)
            {
                if (ramEnabled && ramBanks != null && ramBanks.Length > 0)
                {
                    int offset = address - 0xA000;

                    // Apply the exact same Modulo fix to the RAM banks!
                    int mappedRam = currentRamBank % ramBanks.Length;
                    return ramBanks[mappedRam][offset];
                }
            }

            return 0xFF;
        }
        public void Write(ushort address, byte value)
        {
            // 1. Enable/Disable RAM
            if (address <= 0x1FFF)
            {
                // It only turns on if the lower nibble is exactly 0x0A
                ramEnabled = ((value & 0x0F) == 0x0A);
            }
            // 2. ROM Bank Select (Lower 8 bits)
            else if (address >= 0x2000 && address <= 0x2FFF)
            {
                // MBC5 uses 9 bits for the ROM bank. This sets the bottom 8 bits.
                // I am using bitwise AND to keep the 9th bit, and OR to add the new value.
                currentRomBank = (currentRomBank & 0x100) | value;
            }
            // 3. ROM Bank Select (9th bit)
            else if (address >= 0x3000 && address <= 0x3FFF)
            {
                // This sets just the 9th bit (bit 8)
                currentRomBank = (currentRomBank & 0x00FF) | ((value & 0x01) << 8);
            }
            // 4. RAM Bank Select
            else if (address >= 0x4000 && address <= 0x5FFF)
            {
                // MBC5 can have up to 16 RAM banks, so we keep the lower 4 bits
                currentRamBank = value & 0x0F;
            }
            // 5. Write to External RAM
            else if (address >= 0xA000 && address <= 0xBFFF)
            {
                if (ramEnabled)
                {
                    // Write directly to our passed-in ram array
                    if (ramBanks != null && currentRamBank < ramBanks.Length)
                    {
                        int offset = address - 0xA000;
                        ramBanks[currentRamBank][offset] = value;
                    }
                }
            }
        }
    }
}
