using GameboyTest.MBC;
using System;

using System;

namespace GameboyTest
{
    public class MemoryBus
    {
        private IMbc mbc;
        public enum InterruptType
        {
            VBlank = 0,
            LCDStat = 1,
            Timer = 2,
            Serial = 3,
            Joypad = 4
        }
        // Internal Game Boy Memory Arrays
        private byte[] vram = new byte[0x2000]; // 8KB Video RAM (0x8000 - 0x9FFF)
        private byte[] wram = new byte[0x2000]; // 8KB Working RAM (0xC000 - 0xDFFF)

        // --- NEW HARDWARE ARRAYS ---
        private byte[] oam = new byte[0xA0];    // 160 bytes Sprite RAM (0xFE00 - 0xFE9F)
        private byte[] io = new byte[0x80];     // 128 bytes I/O Registers (0xFF00 - 0xFF7F)
        private byte[] hram = new byte[0x7F];   // 127 bytes High RAM (0xFF80 - 0xFFFE)
        private byte ieRegister = 0x00;         // 1 byte Interrupt Enable (0xFFFF)

        public MemoryBus(IMbc activeMbc)
        {
            this.mbc = activeMbc;
            InitializeHardwareRegisters();

        }

        public byte ReadByte(ushort address)
        {
            // 1. ROM Space
            if (address <= 0x7FFF)
                return mbc.Read(address);

            // 2. Video RAM
            if (address >= 0x8000 && address <= 0x9FFF)
                return vram[address - 0x8000];

            // 3. External Cartridge RAM
            if (address >= 0xA000 && address <= 0xBFFF)
                return mbc.Read(address);

            // 4. Working RAM
            if (address >= 0xC000 && address <= 0xDFFF)
                return wram[address - 0xC000];

            // 5. Echo RAM (Mirrors 0xC000 - 0xDDFF)
            if (address >= 0xE000 && address <= 0xFDFF)
                return wram[address - 0x2000]; // Subtract 0x2000 to map back to WRAM

            // 6. OAM (Object Attribute Memory for Sprites)
            if (address >= 0xFE00 && address <= 0xFE9F)
                return oam[address - 0xFE00];

            // 7. Unusable Space (Nintendo says do not use, usually returns 0xFF)
            if (address >= 0xFEA0 && address <= 0xFEFF)
                return 0xFF;

            // 8. I/O Registers (Joypad, Timers, Audio, LCD)
            if (address >= 0xFF00 && address <= 0xFF7F)
            {
                // NOTE: When you build your Joypad or Timer classes, you will intercept
                // reads here and return dynamic values instead of just reading the array!
                return io[address - 0xFF00];
            }

            // 9. HRAM (High RAM)
            if (address >= 0xFF80 && address <= 0xFFFE)
                return hram[address - 0xFF80];

            // 10. Interrupt Enable Register
            if (address == 0xFFFF)
                return ieRegister;

            return 0xFF;
        }

        public void WriteByte(ushort address, byte value)
        {
            // 1. ROM Space (Bank Switching Commands)
            if (address <= 0x7FFF)
                mbc.Write(address, value);

            // 2. Video RAM
            else if (address >= 0x8000 && address <= 0x9FFF)
                vram[address - 0x8000] = value;

            // 3. External Cartridge RAM/RTC
            else if (address >= 0xA000 && address <= 0xBFFF)
                mbc.Write(address, value);

            // 4. Working RAM
            else if (address >= 0xC000 && address <= 0xDFFF)
                wram[address - 0xC000] = value;

            // 5. Echo RAM (Writing here actually writes to WRAM!)
            else if (address >= 0xE000 && address <= 0xFDFF)
                wram[address - 0x2000] = value;

            // 6. OAM (Sprite Data)
            else if (address >= 0xFE00 && address <= 0xFE9F)
                oam[address - 0xFE00] = value;

            // 7. Unusable Space
            else if (address >= 0xFEA0 && address <= 0xFEFF)
            {
                // Ignored on write
            }

            // 8. I/O Registers
            else if (address >= 0xFF00 && address <= 0xFF7F)
            {
                // NOTE: Similar to reading, you will intercept specific writes here later.
                // Example: Writing to 0xFF46 triggers a DMA transfer to copy sprite data.
                io[address - 0xFF00] = value;
            }

            // 9. HRAM
            else if (address >= 0xFF80 && address <= 0xFFFE)
                hram[address - 0xFF80] = value;

            // 10. Interrupt Enable Register
            else if (address == 0xFFFF)
                ieRegister = value;
        }
        // Helper method to make reading the absolute addresses easier
        private void InitIO(ushort address, byte value)
        {
            io[address - 0xFF00] = value;
        }
        public void RequestInterrupt(InterruptType type)
        {
            // 1. Read the current state of the Interrupt Flag (IF) register
            byte iff = ReadByte(0xFF0F);

            // 2. Flip the specific bit to 1 using a bitwise OR
            iff |= (byte)(1 << (int)type);

            // 3. Write it back to memory
            WriteByte(0xFF0F, iff);
        }
        private void InitializeHardwareRegisters()
        {
            // --- JOYPAD ---
            InitIO(0xFF00, 0xCF); // P1 (Joypad state)

            // --- SERIAL TRANSFER (Link Cable) ---
            InitIO(0xFF01, 0x00); // SB (Serial Transfer Data)
            InitIO(0xFF02, 0x7E); // SC (Serial Transfer Control)

            // --- TIMERS ---
            InitIO(0xFF04, 0xAB); // DIV (Divider Register)
            InitIO(0xFF05, 0x00); // TIMA (Timer Counter)
            InitIO(0xFF06, 0x00); // TMA (Timer Modulo)
            InitIO(0xFF07, 0xF8); // TAC (Timer Control)

            // --- INTERRUPTS ---
            InitIO(0xFF0F, 0xE1); // IF (Interrupt Flag)

            // --- AUDIO (APU / Sound Registers) ---
            // Channel 1 (Sweep/Wave/Play)
            InitIO(0xFF10, 0x80); // NR10
            InitIO(0xFF11, 0xBF); // NR11
            InitIO(0xFF12, 0xF3); // NR12
            InitIO(0xFF13, 0xFF); // NR13 (Usually 00 or FF)
            InitIO(0xFF14, 0xBF); // NR14

            // Channel 2 (Wave/Play)
            InitIO(0xFF16, 0x3F); // NR21
            InitIO(0xFF17, 0x00); // NR22
            InitIO(0xFF18, 0xFF); // NR23 (Usually 00 or FF)
            InitIO(0xFF19, 0xBF); // NR24

            // Channel 3 (Custom Wave)
            InitIO(0xFF1A, 0x7F); // NR30
            InitIO(0xFF1B, 0xFF); // NR31
            InitIO(0xFF1C, 0x9F); // NR32
            InitIO(0xFF1D, 0xFF); // NR33 (Usually 00 or FF)
            InitIO(0xFF1E, 0xBF); // NR34

            // Channel 4 (Noise)
            InitIO(0xFF20, 0xFF); // NR41
            InitIO(0xFF21, 0x00); // NR42
            InitIO(0xFF22, 0x00); // NR43
            InitIO(0xFF23, 0xBF); // NR44

            // Audio Master Controls
            InitIO(0xFF24, 0x77); // NR50 (Channel Control / Volume)
            InitIO(0xFF25, 0xF3); // NR51 (Sound Output Terminal)
            InitIO(0xFF26, 0xF1); // NR52 (Sound ON/OFF)

            // --- LCD / GRAPHICS (PPU) ---
            InitIO(0xFF40, 0x91); // LCDC (LCD Control - Turns screen on and sets layers)
            InitIO(0xFF41, 0x85); // STAT (LCD Status)
            InitIO(0xFF42, 0x00); // SCY (Scroll Y)
            InitIO(0xFF43, 0x00); // SCX (Scroll X)
            InitIO(0xFF44, 0x00); // LY (LCD Y-Coordinate)
            InitIO(0xFF45, 0x00); // LYC (LY Compare)
            InitIO(0xFF46, 0xFF); // DMA (Direct Memory Access Transfer)
            InitIO(0xFF47, 0xFC); // BGP (Background Palette - Maps 0,1,2,3 to actual colors)
            //InitIO(0xFF48, 0xFF); OBP0 (Object Palette 0)
            //InitIO(0xFF49, 0xFF); OBP1 (Object Palette 1)
            InitIO(0xFF4A, 0x00); // WY (Window Y Position)
            InitIO(0xFF4B, 0x00); // WX (Window X Position Minus 7)

            // --- MISCELLANEOUS ---
            InitIO(0xFF50, 0x01); // Bootrom Disable (Setting this to 1 hides the Nintendo logo memory)
        }
    }
}