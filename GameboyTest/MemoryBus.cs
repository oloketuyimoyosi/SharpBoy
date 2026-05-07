using GameboyTest.MBC;
using System;

namespace GameboyTest
{
    public class MemoryBus
    {
        // The active Memory Bank Controller (e.g., Mbc0, Mbc3)
        private IMbc mbc;

        // Internal Game Boy Memory Arrays
        private byte[] vram = new byte[0x2000]; // 8KB Video RAM (0x8000 - 0x9FFF)
        private byte[] wram = new byte[0x2000]; // 8KB Working RAM (0xC000 - 0xDFFF)

        // Pass the interface in, meaning the Bus doesn't care WHICH chip is active!
        public MemoryBus(IMbc activeMbc)
        {
            this.mbc = activeMbc;
        }

        public byte ReadByte(ushort address)
        {
            // 1. ROM Space: Route to the active MBC
            if (address <= 0x7FFF)
            {
                return mbc.Read(address);
            }
            // 2. Video RAM
            else if (address >= 0x8000 && address <= 0x9FFF)
            {
                return vram[address - 0x8000];
            }
            // 3. External Cartridge RAM: Route to the active MBC
            else if (address >= 0xA000 && address <= 0xBFFF)
            {
                return mbc.Read(address);
            }
            // 4. Working RAM
            else if (address >= 0xC000 && address <= 0xDFFF)
            {
                return wram[address - 0xC000];
            }

            // Default fallback if reading unmapped memory or memory we haven't implemented yet
            return 0xFF;
        }

        public void WriteByte(ushort address, byte value)
        {
            // 1. ROM Space: MBC Bank Switching Commands
            if (address <= 0x7FFF)
            {
                mbc.Write(address, value);
            }
            // 2. Video RAM
            else if (address >= 0x8000 && address <= 0x9FFF)
            {
                vram[address - 0x8000] = value;
            }
            // 3. External Cartridge RAM: Save files/RTC data
            else if (address >= 0xA000 && address <= 0xBFFF)
            {
                mbc.Write(address, value);
            }
            // 4. Working RAM
            else if (address >= 0xC000 && address <= 0xDFFF)
            {
                wram[address - 0xC000] = value;
            }
        }
    }
}