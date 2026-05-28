using GameboyTest.MBC;
using GameboyTest.NewFolder;
using System.Diagnostics;

namespace GameboyTest
{
    public class MemoryBus
    {
        private IMbc mbc;
        private Mbc5Camera mbc5Camera;
        public bool isCameraRomActive = false;
        public Timer SystemTimer { get; private set; }
        public PPU ppu { get; private set; }
        public APU apu { get; private set; }
        public Joypad joypad;
        private byte OAM_COUNTER = 0;
        public enum InterruptType
        {
            VBlank = 0,
            LCDStat = 1,
            Timer = 2,
            Serial = 3,
            Joypad = 4
        }
        // Internal Game Boy Memory Arrays
        public byte[] vram = new byte[0x2000]; // 8KB Video RAM (0x8000 - 0x9FFF)
        private byte[] wram = new byte[0x8000]; // Expanded to 32KB (8 banks of 4096 bytes) for GBC!

        // --- NEW HARDWARE ARRAYS ---
        public byte[] oam = new byte[0xA0];    // 160 bytes Sprite RAM (0xFE00 - 0xFE9F)
        public byte[] io = new byte[0x80];     // 128 bytes I/O Registers (0xFF00 - 0xFF7F)
        private byte[] hram = new byte[0x7F];   // 127 bytes High RAM (0xFF80 - 0xFFFE)
        public byte ieRegister = 0x00;         // 1 byte Interrupt Enable (0xFFFF)
        public bool oam_switch = false;
        public byte oam_store = 0;
        public byte RP = 0x00;
        public byte SVBK
        {
            get => (byte)(io[0x70] | 0xF8);
            set => io[0x70] = (byte)(value | 0xF8);
        }

        // --- GBC HDMA STATE ---
        private bool hdmaActive = false;
        private int hdmaBlocksRemaining = 0;
        private ushort hdmaSource = 0;
        private ushort hdmaDest = 0;
        private bool hdmaRegistersDirty = true; // Tracks if the game change
        // --- GBC SPEED SWITCH REGISTER ---
        // 0xFF4D (KEY1). Default is 0x7E (Normal speed, bits 1-6 are always 1)
        public byte KEY1 = 0x7E;
        public MemoryBus(IMbc activeMbc, Action renderCallback) // <-- Added 'isGbc' here
        {
            this.mbc = activeMbc;
            SystemTimer = new Timer(RequestTimerInterrupt);
            ppu = new PPU(RequestLcdInterrupt, RequestVBlankInterrupt, renderCallback, io, PerformHdmaBlock);
            apu = new APU();
            joypad = new Joypad(this);

            // 2. Pass the flag into your new initialization method!
            InitializeHardwareRegisters();
        }




        public byte ReadByte(ushort address)
        {
            // 1. ROM Space
            if (address <= 0x7FFF)
                return mbc.Read(address);

            // 2. Video RAM
            if (address >= 0x8000 && address <= 0x9FFF)
                return ppu.ReadVram(address); ;
            if (address >= 0xA000 && address <= 0xBFFF)
            {
                if (isCameraRomActive) return mbc5Camera.ReadRam(address);
                return mbc.Read(address);
            }
            // 4. Working RAM
            if (address >= 0xC000 && address <= 0xDFFF)
            {
                // 0xC000 - 0xCFFF is ALWAYS Bank 0
                if (address <= 0xCFFF)
                {
                    return wram[address - 0xC000];
                }
                // 0xD000 - 0xDFFF is Switchable Bank 1-7
                else
                {
                    int bank = SVBK & 0x07;
                    if (bank == 0) bank = 1; // Bank 0 maps to Bank 1 in this region!
                    return wram[(bank * 0x1000) + (address - 0xD000)];
                }
            }
            // 5. Echo RAM (Mirrors 0xC000 - 0xDDFF)
            if (address >= 0xE000 && address <= 0xFDFF)
                return ReadByte((ushort)(address - 0x2000));// Subtract 0x2000 to map back to WRAM

            // 6. OAM (Object Attribute Memory for Sprites)

            if (address >= 0xFE00 && address <= 0xFE9F)
                if (oam_switch)
                {
                    return 0xFF;
                }
                else
                {
                    return oam[address - 0xFE00];
                }
                    

            // 7. Unusable Space (Nintendo says do not use, usually returns 0xFF)
            if (address >= 0xFEA0 && address <= 0xFEFF)
                return 0xFF;
            if (address == 0xFF46)
            {
                return oam_store;
            }
            if (address == 0xFF00) { return joypad.ReadRegister(); }
            // 8. I/O Registers (Joypad, Timers, Audio, LCD)
            if (address == 0xFF07) {return  ((byte)((SystemTimer.TAC)|(0xf8))); }

            if (address == 0xFF06) { return SystemTimer.TMA; }
            if (address == 0xFF04) { return SystemTimer.DIV; }
            if (address == 0xFF05) { return SystemTimer.TIMA; }
            // 8. I/O Registers
            // ... (your timer interceptions) ...
            // Tell the game engine: "Yes, the audio is turned on and ready to receive music!"
            // Inside MemoryBus.ReadByte
            if (address >= 0xFF10 && address <= 0xFF3F)
            {
                return apu != null ? apu.ReadRegister(address) : (byte)0xFF;
            }
            //if (address == 0xFF26) return (byte)(io[0x26] & 0xF0); Uncomment it for zelda seasons and ages and mickey mouse racing to work properly. Planning to add an APU.
            if (address == 0xFF68) return ppu.BCPS;
            if (address == 0xFF4F) return ppu.VBK;
            if (address == 0xFF69) return ppu.ReadBgPaletteData(); // BCPD
            if (address == 0xFF6A) return ppu.OCPS;
            if (address == 0xFF6B) return ppu.ReadObjPaletteData(); // OCPD
            if (address == 0xFF4D) return KEY1;
            if (address == 0xFF56) return RP;
           
            if (address == 0xFF70) return SVBK;
            if (address == 0xFF55) return io[0x55];

            if (address == 0xFF56) return RP;
            if (address >= 0xFF51 && address <= 0xFF54) return 0xFF;
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
            if (isCameraRomActive)
            {
                if (address >= 0x0000 && address <= 0x7FFF)
                {
                    mbc5Camera.HandleBankWrites(address, value);
                    // CRITICAL FIX: No 'return;' here! 
                    // The standard MBC below STILL needs to see this write to physically switch the ROM bank!
                }
                else if (address >= 0xA000 && address <= 0xBFFF)
                {
                    mbc5Camera.WriteRam(address, value);
                    return; // Exit early! Do not let the standard MBC overwrite the camera image.
                }
            }
            if (address <= 0x7FFF)
                mbc.Write(address, value);

            // 2. Video RAM
            else if (address >= 0x8000 && address <= 0x9FFF)
            {

                ppu.WriteVram(address, value);
            }

            // 3. External Cartridge RAM/RTC
            else if (address >= 0xA000 && address <= 0xBFFF)
                mbc.Write(address, value);

            // 4. Working RAM
            else if (address >= 0xC000 && address <= 0xDFFF)
            {
                if (address <= 0xCFFF)
                {
                    wram[address - 0xC000] = value;
                }
                else
                {
                    int bank = SVBK & 0x07;
                    if (bank == 0) bank = 1;
                    wram[(bank * 0x1000) + (address - 0xD000)] = value;
                }
            }
            // 5. Echo RAM (Writing here actually writes to WRAM!)
            else if (address >= 0xE000 && address <= 0xFDFF)
            {
                WriteByte((ushort)(address - 0x2000), value);
                return; // Don't forget to return so we don't accidentally write twice!
            }
            // 6. OAM (Sprite Data)
            else if (address >= 0xFE00 && address <= 0xFE9F)
            {
                oam[address - 0xFE00] = value;
                ppu.OAM[address - 0xFE00] = value;
            }
            // 7. Unusable Space
            else if (address >= 0xFEA0 && address <= 0xFEFF)
            {
                // Ignored on write
            }


            // 8. I/O Registers
            if (address == 0xFF04)
            {
                SystemTimer.DIV = 0;
                return;
            }

            // --- APU SQUARE WAVE INTERCEPTS ---
            // Map both Ch1 and Ch2 into our single Square Wave generator for testing!
            // --- CHANNEL 3 ---
            // Inside MemoryBus.WriteByte
            if (address >= 0xFF10 && address <= 0xFF3F)
            {
                if (apu != null) apu.WriteRegister(address, value);
                return; // CRITICAL: Stop the MemoryBus from doing anything else!
            }

            // (No NR20 exists on real hardware!)

            // --- CHANNEL 4 ---

            if (address == 0xFF00) { 

                    joypad.WriteRegister(value); 

                    return; 
            }
            if (address == 0xFF07) {SystemTimer.TAC= value; return; }
            if (address == 0xFF05) { SystemTimer.TIMA = value; io[0x5] = value; return; }
            if (address == 0xFF06) { SystemTimer.TMA = value; return; }

            if (address == 0xFF46)
            {
                DmaTransfer(value);
                
                oam_store = value;
            }

            if (address == 0xFF68) ppu.BCPS = value;
            if (address == 0xFF69) ppu.WriteBgPaletteData(value); // BCPD
            if (address == 0xFF6A) ppu.OCPS = value;
            if (address == 0xFF6B) ppu.WriteObjPaletteData(value);
            if (address == 0xFF56) RP = (byte)(value & 0xC3);
            // --- GBC I/O INTERCEPTS ---
            else if (address == 0xFF4D) KEY1 = (byte)((KEY1 & 0x80) | (value & 0x01) | 0x7E);
            else if (address == 0xFF4F) ppu.VBK = value;

            // THE FIX: Directly update the active pointers. No more stale 'io' arrays or dirty flags!
            else if (address == 0xFF51) hdmaSource = (ushort)((hdmaSource & 0x00FF) | (value << 8));
            else if (address == 0xFF52) hdmaSource = (ushort)((hdmaSource & 0xFF00) | (value & 0xF0)); // Lower 4 bits ignored
            else if (address == 0xFF53) hdmaDest = (ushort)((hdmaDest & 0x00FF) | ((value & 0x1F) << 8) | 0x8000); // Forced to VRAM
            else if (address == 0xFF54) hdmaDest = (ushort)((hdmaDest & 0xFF00) | (value & 0xF0)); // Lower 4 bits ignored
            else if (address == 0xFF16) apu.NR21 = value;
            else if (address == 0xFF17) apu.NR22 = value;
            else if (address == 0xFF18) apu.NR23 = value;
            else if (address == 0xFF19) apu.NR24 = value;
            else if (address == 0xFF55) WriteHdma5(value);
            else if (address >= 0xFF00 && address <= 0xFF7F)
            {
                // NOTE: Similar to reading, you will intercept specific writes here later.
                // Example: Writing to 0xFF46 triggers a DMA transfer to copy sprite data.
                if (io[0xF] == 228 && (value == 0) && address == 0xFF0F)
                {
                    throw new Exception($"{value}");
                }
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
            io[address - 0xff00] = value;
            //WriteByte(address, value);
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
            InitIO(0xFF00, 0xCF); // P1 (Joypad state)

            // --- SERIAL TRANSFER (Link Cable) ---
            InitIO(0xFF01, 0x00); // SB (Serial Transfer Data)
            InitIO(0xFF02, 0x7E); // SC (Serial Transfer Control)

            // --- TIMERS ---
            InitIO(0xFF04, 0xAB); // DIV (Divider Register)
            InitIO(0xFF05, 0x00); // TIMA (Timer Counter)
            InitIO(0xFF06, 0x00); // TMA (Timer Modulo)
            InitIO(0xFF07, 0xFD); // TAC (Timer Control)

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
            InitIO(0xFF44, 0); // LY (LCD Y-Coordinate) // Set the PPU's internal LY to match the hardware register
            InitIO(0xFF45, 0x00); // LYC (LY Compare)
            InitIO(0xFF46, 0xFF); // DMA (Direct Memory Access Transfer)
            InitIO(0xFF47, 0xFC); // BGP (Background Palette - Maps 0,1,2,3 to actual colors)
            //InitIO(0xFF48, 0xFF); OBP0 (Object Palette 0)
            //InitIO(0xFF49, 0xFF); OBP1 (Object Palette 1)
            InitIO(0xFF4A, 0x00); // WY (Window Y Position)
            InitIO(0xFF4B, 0x00); // WX (Window X Position Minus 7)
            InitIO(0xFF4F, 0xFE); // VBK (VRAM Bank) defaults to Bank 0
            InitIO(0xFF70, 0xF9); // SVBK (WRAM Bank) defaults to Bank 1
            // --- MISCELLANEOUS ---
            InitIO(0xFF50, 0x01);
        }
        private void RequestTimerInterrupt()
        {
            // The Timer Interrupt is Bit 2 of the Interrupt Flag (IF) register at 0xFF0F.
            // We will read the current IF value, set Bit 2 to true, and write it back!
            byte currentIF = ReadByte(0xFF0F);
            WriteByte(0xFF0F, (byte)(currentIF | 0x04));
        }
        private void RequestVBlankInterrupt()
        {
            // V-Blank is Bit 0 of the IF Register (0xFF0F)
            byte currentIF = ReadByte(0xFF0F);
            WriteByte(0xFF0F, (byte)(currentIF | 0x01));
        }

        private void RequestLcdInterrupt()
        {
            // LCD STAT is Bit 1 of the IF Register (0xFF0F)
            byte currentIF = ReadByte(0xFF0F);
            WriteByte(0xFF0F, (byte)(currentIF | 0x02));
        }
        public void DmaTransfer(byte value)
        {
            byte data;
            ushort sourceAddress = (ushort)(value << 8);
            ushort address = (ushort)(sourceAddress + OAM_COUNTER);
            
            // OAM DMA always copies exactly 160 bytes (40 sprites * 4 bytes each)
            if ((address >= 0xFE00) && (address <= 0XFE9F))
            {
                
                data = wram[(address - 0xF000) + 0x1000];

            }
            else if ((address >= 0xE000) && (address <= 0XFDFF))
            {
                data = wram[(address - 0xE000)];

            }
            else if ((address >= 0xFF00) && (address <= 0XFFFF))
            {

                data = wram[(address - 0xF000) + 0x1000];

            }
            else
            {
                
                data = ReadByte(address);
            }
            WriteByte((ushort)(0xFE00 + OAM_COUNTER), data);
            OAM_COUNTER++;
            oam_switch = true;

            if (OAM_COUNTER >= 0xA1)
            {
                OAM_COUNTER = 0;
                oam_switch = false;
            }
            
        }
        // --- GBC HDMA LOGIC ---
        private void WriteHdma5(byte value)
        {
            if (hdmaActive)
            {
                if ((value & 0x80) == 0)
                {
                    hdmaActive = false;
                    io[0x55] = (byte)((hdmaBlocksRemaining - 1) | 0x80);
                }
                return;
            }



            hdmaBlocksRemaining = (value & 0x7F) + 1;
            bool isHBlankDma = (value & 0x80) != 0;

            if (!isHBlankDma)
            {
                hdmaActive = true; // FIX: We have to actually turn the DMA on!

                while (hdmaBlocksRemaining > 0) //FIXES DRAGON BALL Z , ZELD SEASONS AND AGES AND MICKEY MOUSE RACING 
                {
                    PerformHdmaBlock();
                }
            }
            else
            {
                hdmaActive = true;
                io[0x55] = (byte)(hdmaBlocksRemaining - 1);
                if ((ppu.LCDC & 0x80) != 0 && (ppu.STAT & 0x03) == 0) PerformHdmaBlock();
            }
        }

        public void PerformHdmaBlock()
        {
            if (!hdmaActive) return;

            for (int i = 0; i < 16; i++)
            {
                byte data = ReadByte((ushort)(hdmaSource + i));
                WriteByte((ushort)(hdmaDest + i), data);
            }

            // THE FUN FACT: Incrementing the internal trackers so they resume smoothly!
            hdmaSource += 16;
            hdmaDest += 16;

            if (hdmaDest > 0x9FFF) hdmaDest = (ushort)(0x8000 + (hdmaDest & 0x1FFF));

            hdmaBlocksRemaining--;

            if (hdmaBlocksRemaining == 0)
            {
                hdmaActive = false;
                io[0x55] = 0xFF;
            }
            else
            {
                io[0x55] = (byte)(hdmaBlocksRemaining - 1);
            }
        }
        // --- ADD THIS METHOD INSIDE BUS.CS ---
        public void AttachCameraMapper(Mbc5Camera cameraMapper)
        {
            this.mbc5Camera = cameraMapper;
            this.isCameraRomActive = true;
        }

        // Optional but highly recommended: A way to detach it when loading a normal game
        public void DetachCameraMapper()
        {
            this.mbc5Camera = null;
            this.isCameraRomActive = false;
        }
        //
    }
}