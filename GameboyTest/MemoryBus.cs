using System;

namespace GameboyTest
{
    public class MemoryBus
    {
        private Cartridge cartridge;
        private byte[] vram = new byte[0x2000]; // 8KB Video RAM
        private byte[] wram = new byte[0x2000]; // 8KB Working RAM

        // Pass the loaded cartridge into the bus
        public MemoryBus(Cartridge loadedCartridge)
        {
            this.cartridge = loadedCartridge;
        }

        // The CPU calls this to read data
        public byte ReadByte(ushort address)
        {
            // If the address is between 0x0000 and 0x7FFF, read from your RomData!
            if (address <= 0x7FFF)
            {
                return cartridge.RomData[address];
            }
            // If it's looking for Video RAM
            else if (address >= 0x8000 && address <= 0x9FFF)
            {
                return vram[address - 0x8000];
            }
            // If it's looking for Working RAM
            else if (address >= 0xC000 && address <= 0xDFFF)
            {
                return wram[address - 0xC000];
            }

            // Default fallback if reading unmapped memory
            return 0xFF;
        }

        // The CPU calls this to save data
        public void WriteByte(ushort address, byte value)
        {
            // Note: You normally can't write to ROM (it's Read-Only!)
            // So if address <= 0x7FFF, we usually ignore it or handle Bank Switching later.

            if (address >= 0x8000 && address <= 0x9FFF)
            {
                vram[address - 0x8000] = value;
            }
            else if (address >= 0xC000 && address <= 0xDFFF)
            {
                wram[address - 0xC000] = value;
            }
        }
    }
}