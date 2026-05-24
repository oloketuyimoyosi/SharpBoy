using GameboyTest.NewFolder;
using System.Diagnostics;
using System.Net;
using System.Timers;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Collections.Generic;
using System;

namespace GameboyTest
{
    public class PPU
    {
        // --- MEMORY REGISTERS ---
        public byte LCDC { get => io[0x40]; set => io[0x40] = value; }
        public byte STAT { get => io[0x41]; set => io[0x41] = value; }
        public byte LY { get => io[0x44]; set => io[0x44] = value; }
        public byte LYC { get => io[0x45]; set => io[0x45] = value; }

        // --- SKIASHARP COLORS (AARRGGBB) ---
        private readonly uint[] Colors = {
            0xFFCADC9F,
            0xFF8BAC0F,
            0xFF306230,
            0xFF0F380F
        };

        // --- TIMING CONSTANTS ---
        private const int SCANLINE_CYCLES = 456;
        private const int MODE_2_BOUND = 80;

        // --- INTERNAL STATE ---
        private int scanlineCounter = 456;
        private byte windowLineCounter = 0;
        private bool windowYTriggered = false;
        private bool isLine153 = false;

        // --- GBC PALETTE RAM ---
        public byte[] CgbBgPaletteRam { get; private set; } = new byte[64];
        public byte[] CgbObjPaletteRam { get; private set; } = new byte[64];
        public bool IsGbc = false;

        public byte BCPS { get => io[0x68]; set => io[0x68] = value; }
        public byte OCPS { get => io[0x6A]; set => io[0x6A] = value; }

        private System.IO.StreamWriter debugLog;
        private bool tick = false;
        private bool ppu_mode_on_off = false;
        private Action requestLcdInterrupt;
        private Action requestVBlankInterrupt;
        private Action requestFrameRender;
        private Action performHdmaCallback;
        // --- STRICT HARDWARE TRACKERS ---
        private int current_ppu_mode = 0;
        private bool stat_interrupt_line = false;
        private bool isFirstFrame = false;
        public uint[] DisplayBuffer { get; private set; } = new uint[160 * 144];
        public readonly object BufferLock = new object();
        public uint[] FrameBuffer { get; private set; } = new uint[160 * 144];

        private byte[] io { get; set; }
        private bool ly_check_triggered { get; set; } = false;
        private bool? lcd_on_off = null;
        private bool ly_match_stored = false;
        private bool ly_match = false;

        // --- PANDOCS PIXEL FIFO TRACKERS ---
        private struct FifoPixel
        {
            public int ColorNum;
            public byte Palette;
            public bool Priority;
        }

        private Queue<FifoPixel> bgFifo = new Queue<FifoPixel>(16);
        private Queue<FifoPixel> objFifo = new Queue<FifoPixel>(16);

        private int pixelsPushedThisLine = 0;
        private int pixelsToDrop = 0;

        private int fetcherState = 0;
        private int fetcherCycle = 0;
        private int fetchX = 0;
        private int fetcherScreenX = 0;
        private bool inWindow = false;

        private byte fetchTileNo = 0;
        private byte fetchAttributes = 0;
        private byte fetchDataLow = 0;
        private byte fetchDataHigh = 0;

        // --- HARDWARE STALLS & CACHES ---
        private List<SpriteData> lineSprites = new List<SpriteData>(10);
        private int fetcherStallCycles = 0;
        private int mode3DelayCycles = 0;

        private struct SpriteData
        {
            public int Y, X, Tile, Attributes, OamIndex;
        }

        public byte SCY { get => io[0x42]; set => io[0x42] = value; }
        public byte SCX { get => io[0x43]; set => io[0x43] = value; }
        public byte WY { get => io[0x4A]; set => io[0x4A] = value; }
        public byte WX { get => io[0x4B]; set => io[0x4B] = value; }
        public byte BGP { get => io[0x47]; set => io[0x47] = value; }
        public byte OBP0 { get => io[0x48]; set => io[0x48] = value; }
        public byte OBP1 { get => io[0x49]; set => io[0x49] = value; }

        public byte[] VRAM { get; private set; } = new byte[0x4000];
        public byte[] OAM { get; private set; } = new byte[160];

        public byte VBK
        {
            get => (byte)(io[0x4F] | 0xFE);
            set => io[0x4F] = (byte)(value | 0xFE);
        }

        public PPU(Action lcdInterrupt, Action vBlankInterrupt, Action renderCallback, byte[] io, Action hdmaCallback)
        {
            this.requestLcdInterrupt = lcdInterrupt;
            this.requestVBlankInterrupt = vBlankInterrupt;
            this.requestFrameRender = renderCallback;
            this.io = io;
            this.performHdmaCallback = hdmaCallback;
        }

        private bool IsLcdEnabled() => (LCDC & 0x80) != 0;

        // =========================================================
        // 1. MAIN EXECUTION LOOP
        // =========================================================
        public void Tick(int cycles, ushort PC, bool halted, byte ie, bool IME, bool Interrupt_on_Line)
        {
            if (!IsLcdEnabled())
            {
                scanlineCounter = SCANLINE_CYCLES;
                LY = 0;
                pixelsPushedThisLine = 0;
                UpdateStatus(PC, halted, ie, IME, Interrupt_on_Line);

                isFirstFrame = true; // <-- ADD THIS

                return;
            }

            scanlineCounter -= cycles;

            if (isLine153 && scanlineCounter <= (SCANLINE_CYCLES - 4))
            {
                LY = 0;
            }

            int current_mode = STAT & 0x03;
            int next_mode = calculatemode(scanlineCounter, LY, IsLcdEnabled(), PC);

            int mode3CyclesToRun = 0;
            if (current_mode == 3)
            {
                mode3CyclesToRun = cycles;
            }
            else if (current_mode == 2 && next_mode == 3)
            {
                int mode3StartCycle = SCANLINE_CYCLES - 80;
                mode3CyclesToRun = mode3StartCycle - scanlineCounter;
            }

            if (current_mode == 2 && next_mode == 3)
            {
                bgFifo.Clear();
                objFifo.Clear();
                pixelsToDrop = SCX % 8;
                fetcherScreenX = -(SCX % 8);
                fetcherState = 0;
                fetcherCycle = 0;
                fetchX = 0;
                inWindow = false;

                fetcherStallCycles = 0;
                BuildLineSprites();

                // TRUE HARDWARE DELAY: No more pre-fetching time machines!
                // We let the fetcher read SCX organically cycle-by-cycle.
                mode3DelayCycles = 12;
            }
            for (int i = 0; i < mode3CyclesToRun; i++)
            {
                TickPixelFifo();
            }

            if (scanlineCounter <= 0)
            {
                if (inWindow) windowLineCounter++;

                scanlineCounter += SCANLINE_CYCLES;
                pixelsPushedThisLine = 0;

                if (isLine153)
                {
                    isLine153 = false;
                    LY = 0;
                    windowLineCounter = 0;
                    windowYTriggered = false;
                }
                else
                {
                    LY++;
                    if (LY == 144)
                    {
                        isFirstFrame = false; // <-- ADD THIS: Warmup frame is over!

                        if (IsLcdEnabled()) requestVBlankInterrupt();
                        lock (BufferLock)
                        {
                            Array.Copy(FrameBuffer, DisplayBuffer, FrameBuffer.Length);
                        }
                        requestFrameRender();
                    }
                    else if (LY == 153)
                    {
                        isLine153 = true;
                    }
                }

                if (LY == WY) windowYTriggered = true;

            }

            UpdateStatus(PC, halted, ie, IME, Interrupt_on_Line);
        }

        // =========================================================
        // 2. TRUE PIXEL FIFO & MULTIPLEXER
        // =========================================================
        // =========================================================
        // 2. TRUE PIXEL FIFO & MULTIPLEXER
        // =========================================================
        private void TickPixelFifo()
        {
            if (pixelsPushedThisLine >= 160) return;

            if (mode3DelayCycles > 0)
            {
                mode3DelayCycles--;
            }
            else if (bgFifo.Count > 0) // THE FIX: Allow LCD to draw instantly!
            {
                if (pixelsToDrop > 0)
                {
                    bgFifo.Dequeue();
                    objFifo.Dequeue();
                    pixelsToDrop--;
                }
                else
                {
                    if ((LCDC & 0x20) != 0 && windowYTriggered && pixelsPushedThisLine >= (WX - 7) && !inWindow)
                    {
                        bgFifo.Clear();
                        objFifo.Clear();
                        fetcherState = 0;
                        fetchX = 0;
                        inWindow = true;

                        if (WX < 7)
                        {
                            pixelsToDrop = 7 - WX;
                            fetcherScreenX = pixelsPushedThisLine - (7 - WX);
                        }
                        else
                        {
                            pixelsToDrop = 0;
                            fetcherScreenX = pixelsPushedThisLine;
                        }

                        mode3DelayCycles = 6;
                        return;
                    }

                    FifoPixel bgPix = bgFifo.Dequeue();
                    FifoPixel objPix = objFifo.Dequeue();

                    FrameBuffer[LY * 160 + pixelsPushedThisLine] = MixPixels(bgPix, objPix);
                    pixelsPushedThisLine++;
                }
            }

            // Hardware Stalls (LCD continues to drain FIFO while Fetcher pauses!)
            if (fetcherStallCycles > 0)
            {
                fetcherStallCycles--;
                return;
            }

            fetcherCycle++;
            if (fetcherCycle >= 2)
            {
                fetcherCycle = 0;
                StepFetcher();
            }
        }

        private uint MixPixels(FifoPixel bg, FifoPixel obj)
        {
            if (isFirstFrame) return Colors[0];

            if (obj.ColorNum != 0)
            {
                bool bgWins = false;

                if (IsGbc)
                {
                    if ((LCDC & 0x01) != 0)
                    {
                        if (bg.Priority && bg.ColorNum != 0) bgWins = true;
                        if (obj.Priority && bg.ColorNum != 0) bgWins = true;
                    }
                }
                else
                {
                    if (obj.Priority && bg.ColorNum != 0) bgWins = true;
                }

                if (!bgWins)
                {
                    if (IsGbc) return GetGbcColor(obj.Palette, obj.ColorNum, true);

                    // LIVE READ: Apply real-time DMG Sprite Palette
                    byte liveObp = (obj.Palette == 1) ? OBP1 : OBP0;
                    return Colors[(liveObp >> (obj.ColorNum * 2)) & 3];
                }
            }

            if (IsGbc) return GetGbcColor(bg.Palette, bg.ColorNum, false);

            // LIVE READ: Apply real-time DMG Background Palette
            return Colors[(BGP >> (bg.ColorNum * 2)) & 3];
        }

        // =========================================================
        // 3. FETCH STATE MACHINE & SPRITE CACHE
        // =========================================================
        private void BuildLineSprites()
        {
            lineSprites.Clear();
            if ((LCDC & 0x02) == 0) return;

            bool use8x16 = (LCDC & 0x04) != 0;
            int spriteHeight = use8x16 ? 16 : 8;

            for (int i = 0; i < 160 && lineSprites.Count < 10; i += 4)
            {
                int yPos = OAM[i] - 16;
                if (LY >= yPos && LY < (yPos + spriteHeight))
                {
                    lineSprites.Add(new SpriteData { Y = yPos, X = OAM[i + 1] - 8, Tile = OAM[i + 2], Attributes = OAM[i + 3], OamIndex = i });
                }
            }

            lineSprites.Sort((a, b) => {
                if (IsGbc) return b.OamIndex.CompareTo(a.OamIndex);
                int xCmp = b.X.CompareTo(a.X);
                if (xCmp != 0) return xCmp;
                return b.OamIndex.CompareTo(a.OamIndex);
            });
        }

        private void StepFetcher()
        {
            switch (fetcherState)
            {
                case 0:
                    ushort tileMapBase = (ushort)(inWindow ? (((LCDC & 0x40) != 0) ? 0x1C00 : 0x1800) : (((LCDC & 0x08) != 0) ? 0x1C00 : 0x1800));
                    int mapX = inWindow ? (fetchX & 255) : ((fetchX + (SCX / 8) * 8) & 255);
                    int mapY = inWindow ? windowLineCounter : ((LY + SCY) & 255);

                    ushort mapAddress = (ushort)(tileMapBase + ((mapY / 8) * 32) + (mapX / 8));
                    fetchTileNo = VRAM[mapAddress];
                    if (IsGbc) fetchAttributes = VRAM[mapAddress + 0x2000];
                    fetcherState = 1;
                    break;

                case 1:
                    bool tileDataSigned = (LCDC & 0x10) == 0;
                    ushort tileDataAddress = (ushort)(tileDataSigned ? 0x0800 : 0x0000);
                    int tileNum = fetchTileNo;
                    if (tileDataSigned) { tileNum = (sbyte)tileNum; tileNum += 128; }

                    int vramBankOffset = (IsGbc && (fetchAttributes & 0x08) != 0) ? 0x2000 : 0x0000;
                    bool yFlip = IsGbc && (fetchAttributes & 0x40) != 0;

                    int lineY = inWindow ? windowLineCounter : (LY + SCY);
                    int lineInTile = lineY & 7;
                    if (yFlip) lineInTile = 7 - lineInTile;

                    fetchDataLow = VRAM[tileDataAddress + (tileNum * 16) + (lineInTile * 2) + vramBankOffset];
                    fetcherState = 2;
                    break;

                case 2:
                    tileDataSigned = (LCDC & 0x10) == 0;
                    ushort tileDataAddress2 = (ushort)(tileDataSigned ? 0x0800 : 0x0000);
                    int tileNum2 = fetchTileNo;
                    if (tileDataSigned) { tileNum2 = (sbyte)tileNum2; tileNum2 += 128; }

                    int vramBankOffset2 = (IsGbc && (fetchAttributes & 0x08) != 0) ? 0x2000 : 0x0000;
                    bool yFlip2 = IsGbc && (fetchAttributes & 0x40) != 0;

                    int lineY2 = inWindow ? windowLineCounter : (LY + SCY);
                    int lineInTile2 = lineY2 & 7;
                    if (yFlip2) lineInTile2 = 7 - lineInTile2;

                    fetchDataHigh = VRAM[tileDataAddress2 + (tileNum2 * 16) + (lineInTile2 * 2) + vramBankOffset2 + 1];
                    fetcherState = 3;
                    break;

                case 3:
                    if (bgFifo.Count <= 8)
                    {
                        int spritesHitThisChunk = 0;
                        FifoPixel[] spriteOverlay = GetSpriteOverlayForChunk(fetcherScreenX, out spritesHitThisChunk);

                        bool xFlip = IsGbc && (fetchAttributes & 0x20) != 0;
                        byte palette = IsGbc ? (byte)(fetchAttributes & 0x07) : (byte)0;
                        bool bgPriority = IsGbc && (fetchAttributes & 0x80) != 0;

                        for (int i = 0; i < 8; i++)
                        {
                            int bit = xFlip ? i : (7 - i);
                            int colorNum = (((fetchDataHigh >> bit) & 1) << 1) | ((fetchDataLow >> bit) & 1);

                            if (!IsGbc && (LCDC & 0x01) == 0) colorNum = 0;

                            bgFifo.Enqueue(new FifoPixel { ColorNum = colorNum, Palette = palette, Priority = bgPriority });
                            objFifo.Enqueue(spriteOverlay[i]);
                        }

                        fetchX += 8;
                        fetcherScreenX += 8;
                        fetcherState = 0;
                        fetcherStallCycles += (spritesHitThisChunk * 6);
                    }
                    break;
            }
        }

        private FifoPixel[] GetSpriteOverlayForChunk(int startScreenX, out int spritesHit)
        {
            spritesHit = 0;
            FifoPixel[] overlay = new FifoPixel[8];
            for (int i = 0; i < 8; i++) overlay[i].ColorNum = 0;

            if ((LCDC & 0x02) == 0) return overlay;

            bool use8x16 = (LCDC & 0x04) != 0;
            int spriteHeight = use8x16 ? 16 : 8;

            foreach (var sprite in lineSprites)
            {
                if (sprite.X >= startScreenX + 8 || sprite.X + 8 <= startScreenX) continue;

                spritesHit++;

                int line = LY - sprite.Y;
                if ((sprite.Attributes & 0x40) != 0) line = (spriteHeight - 1) - line;

                int tileLocation = sprite.Tile;
                if (use8x16) tileLocation &= 0xFE;

                int vramBankOffset = (IsGbc && (sprite.Attributes & 0x08) != 0) ? 0x2000 : 0x0000;
                ushort dataAddress = (ushort)((tileLocation * 16) + (line * 2) + vramBankOffset);

                byte data1 = VRAM[dataAddress];
                byte data2 = VRAM[dataAddress + 1];

                for (int i = 0; i < 8; i++)
                {
                    int pixelScreenX = startScreenX + i;

                    if (pixelScreenX >= sprite.X && pixelScreenX < sprite.X + 8)
                    {
                        int tilePixel = pixelScreenX - sprite.X;
                        int colorBit = ((sprite.Attributes & 0x20) != 0) ? tilePixel : (7 - tilePixel);
                        int colorNum = (((data2 >> colorBit) & 1) << 1) | ((data1 >> colorBit) & 1);

                        if (colorNum != 0)
                        {
                            overlay[i].ColorNum = colorNum;
                            overlay[i].Palette = IsGbc ? (byte)(sprite.Attributes & 0x07) : (byte)(((sprite.Attributes & 0x10) != 0) ? 1 : 0);
                            overlay[i].Priority = (sprite.Attributes & 0x80) != 0;
                        }
                    }
                }
            }
            return overlay;
        }

        // =========================================================
        // 4. YOUR EXISTING CODE (STATUS, QUIRKS, & SKIASHARP)
        // =========================================================
        // =========================================================
        // 4. BULLETPROOF HARDWARE STAT INTERRUPT STATE MACHINE
        // =========================================================
        // =========================================================
        // 4. BULLETPROOF HARDWARE STAT INTERRUPT STATE MACHINE
        // =========================================================
        // =========================================================
        // 4. BULLETPROOF HARDWARE STAT INTERRUPT STATE MACHINE
        // =========================================================
        private void UpdateStatus(ushort PC, bool halted, byte ie, bool IME, bool Interrupt_on_Line)
        {
            int new_mode = 0;
            bool ly_match_condition = false;

            if (!IsLcdEnabled())
            {
                scanlineCounter = SCANLINE_CYCLES;
                LY = 0;
                new_mode = 0;

                // FROZEN COMPARATOR (Passes r2 intr): Retains the LYC=LY flag state while unpowered
                ly_match_condition = (STAT & 0x04) != 0;
            }
            else
            {
                new_mode = calculatemode(scanlineCounter, LY, IsLcdEnabled(), PC);
                ly_match_condition = (LY == LYC);

                if (new_mode == 0 && current_ppu_mode != 0)
                {
                    if (IsGbc) performHdmaCallback();
                }
            }

            // PROTECT READ-ONLY BITS: Override CPU memory corruption
            int new_status = (STAT & 0xF8) | 0x80;
            new_status |= new_mode;
            if (ly_match_condition) new_status |= 0x04;
            STAT = (byte)new_status;

            // CONTINUOUS OR-GATE EVALUATION (Passes r4 no intr)
            bool mode0_int = (new_mode == 0) && ((new_status & 0x08) != 0);
            bool mode1_int = (new_mode == 1) && ((new_status & 0x10) != 0);
            bool mode2_int = (new_mode == 2) && ((new_status & 0x20) != 0);
            bool lyc_int = ly_match_condition && ((new_status & 0x40) != 0);

            bool new_interrupt_line = mode0_int || mode1_int || mode2_int || lyc_int;

            // LEGO RACERS FIX: Hardware STAT Gap
            if (IsLcdEnabled() && current_ppu_mode != new_mode && !lyc_int)
            {
                stat_interrupt_line = false;
            }

            // RISING EDGE TRIGGER
            if (!stat_interrupt_line && new_interrupt_line)
            {
                requestLcdInterrupt();
            }

            stat_interrupt_line = new_interrupt_line;
            current_ppu_mode = new_mode;
        }

        private int calculatemode(int scanline_cycles, int current_line, bool lcd_enabled, ushort PC)
        {
            if (!lcd_enabled) return 0;
            if (current_line >= 144 || isLine153) return 1;

            // MODE 2 SKIP (Passes r1 step 3): First scanline skips Mode 2 upon waking up
            if (isFirstFrame && current_line == 0 && scanline_cycles >= (SCANLINE_CYCLES - 80))
            {
                return 0;
            }

            if (scanline_cycles >= (SCANLINE_CYCLES - 80)) return 2;
            if (pixelsPushedThisLine < 160) return 3;

            return 0;
        }
        private int updateStatus(int status, int mode)
        {
            status = (status & 0xFC) | (mode);
            if (ly_match) status |= 0x4;
            else status &= 0xFB;
            return status;
        }

        public byte ReadBgPaletteData() => CgbBgPaletteRam[BCPS & 0x3F];
        public void WriteBgPaletteData(byte value)
        {
            CgbBgPaletteRam[BCPS & 0x3F] = value;
            if ((BCPS & 0x80) != 0)
            {
                byte nextIndex = (byte)((BCPS & 0x3F) + 1);
                BCPS = (byte)(0x80 | (nextIndex & 0x3F));
            }
        }

        public byte ReadObjPaletteData() => CgbObjPaletteRam[OCPS & 0x3F];
        public void WriteObjPaletteData(byte value)
        {
            CgbObjPaletteRam[OCPS & 0x3F] = value;
            if ((OCPS & 0x80) != 0)
            {
                byte nextIndex = (byte)((OCPS & 0x3F) + 1);
                OCPS = (byte)(0x80 | (nextIndex & 0x3F));
            }
        }

        public byte ReadVram(ushort address)
        {
            int offset = address - 0x8000;
            int bank = VBK & 0x01;
            return VRAM[(bank * 0x2000) + offset];
        }

        public void WriteVram(ushort address, byte value)
        {
            int offset = address - 0x8000;
            int bank = VBK & 0x01;
            VRAM[(bank * 0x2000) + offset] = value;
        }

        // --- SKIASHARP DEBUG VIEWER METHODS (UNCHANGED) ---
        public uint[] GetVramTexture(int paletteChoice = 0, int gbcPaletteIndex = 0)
        {
            uint[] vramBuffer = new uint[256 * 256];
            byte targetPalette = BGP;
            bool isSpritePalette = false;
            if (paletteChoice == 1) { targetPalette = OBP0; isSpritePalette = true; }
            if (paletteChoice == 2) { targetPalette = OBP1; isSpritePalette = true; }

            for (int tileIndex = 0; tileIndex < 1024; tileIndex++)
            {
                int bankOffset = (tileIndex >= 512) ? 0x2000 : 0x0000;
                int localTileIndex = tileIndex % 512;
                int gridX = tileIndex % 32;
                int gridY = tileIndex / 32;
                int pixelStartX = gridX * 8;
                int pixelStartY = gridY * 8;

                for (int y = 0; y < 8; y++)
                {
                    int dataAddress = (localTileIndex * 16) + (y * 2) + bankOffset;
                    byte data1 = VRAM[dataAddress];
                    byte data2 = VRAM[dataAddress + 1];

                    for (int x = 0; x < 8; x++)
                    {
                        int colorBit = 7 - x;
                        int colorNum = (((data2 >> colorBit) & 1) << 1) | ((data1 >> colorBit) & 1);
                        uint finalColor;
                        if (IsGbc) finalColor = GetGbcColor(gbcPaletteIndex, colorNum, isSpritePalette);
                        else
                        {
                            int paletteVal = (targetPalette >> (colorNum * 2)) & 3;
                            finalColor = Colors[paletteVal];
                        }

                        int drawX = pixelStartX + x;
                        int drawY = pixelStartY + y;
                        vramBuffer[(drawY * 256) + drawX] = finalColor;
                    }
                }
            }
            return vramBuffer;
        }

        public uint[] GetBackgroundMapTexture(bool useMap2 = false, bool showSprites = false)
        {
            uint[] bgBuffer = new uint[256 * 256];
            ushort mapBase = (ushort)(useMap2 ? 0x1C00 : 0x1800);
            bool tileDataSigned = (LCDC & 0x10) == 0;
            ushort tileDataBase = (ushort)(tileDataSigned ? 0x0800 : 0x0000);

            for (int tileY = 0; tileY < 32; tileY++)
            {
                for (int tileX = 0; tileX < 32; tileX++)
                {
                    int mapIndex = mapBase + (tileY * 32) + tileX;
                    int tileNum = VRAM[mapIndex];
                    if (tileDataSigned) { tileNum = (sbyte)tileNum; tileNum += 128; }

                    int vramBankOffset = 0;
                    int paletteIndex = 0;
                    bool xFlip = false;
                    bool yFlip = false;

                    if (IsGbc)
                    {
                        byte attributes = VRAM[mapIndex + 0x2000];
                        paletteIndex = attributes & 0x07;
                        vramBankOffset = ((attributes & 0x08) != 0) ? 0x2000 : 0x0000;
                        xFlip = (attributes & 0x20) != 0;
                        yFlip = (attributes & 0x40) != 0;
                    }

                    for (int y = 0; y < 8; y++)
                    {
                        int lineInTile = yFlip ? 7 - y : y;
                        int dataAddress = tileDataBase + (tileNum * 16) + (lineInTile * 2) + vramBankOffset;
                        byte data1 = VRAM[dataAddress];
                        byte data2 = VRAM[dataAddress + 1];

                        for (int x = 0; x < 8; x++)
                        {
                            int tilePixelX = xFlip ? 7 - x : x;
                            int colorBit = 7 - tilePixelX;
                            int colorNum = (((data2 >> colorBit) & 1) << 1) | ((data1 >> colorBit) & 1);

                            uint finalColor;
                            if (IsGbc) finalColor = GetGbcColor(paletteIndex, colorNum, false);
                            else
                            {
                                int paletteVal = (BGP >> (colorNum * 2)) & 3;
                                finalColor = Colors[paletteVal];
                            }

                            int pixelX = (tileX * 8) + x;
                            int pixelY = (tileY * 8) + y;
                            bgBuffer[(pixelY * 256) + pixelX] = finalColor;
                        }
                    }
                }
            }

            if (showSprites)
            {
                bool use8x16 = (LCDC & 0x04) != 0;
                int spriteHeight = use8x16 ? 16 : 8;

                for (int i = 39; i >= 0; i--)
                {
                    int oamIndex = i * 4;
                    int screenY = OAM[oamIndex] - 16;
                    int screenX = OAM[oamIndex + 1] - 8;
                    int tileLocation = OAM[oamIndex + 2];
                    int attributes = OAM[oamIndex + 3];

                    int mapY = (screenY + SCY) & 255;
                    int mapX = (screenX + SCX) & 255;

                    bool yFlip = (attributes & 0x40) != 0;
                    bool xFlip = (attributes & 0x20) != 0;

                    int paletteIndex = 0;
                    int vramBankOffset = 0;
                    byte dmgPalette = ((attributes & 0x10) != 0) ? OBP1 : OBP0;

                    if (IsGbc)
                    {
                        paletteIndex = attributes & 0x07;
                        vramBankOffset = ((attributes & 0x08) != 0) ? 0x2000 : 0x0000;
                    }

                    if (use8x16) tileLocation &= 0xFE;

                    for (int y = 0; y < spriteHeight; y++)
                    {
                        int line = yFlip ? (spriteHeight - 1 - y) : y;
                        ushort dataAddress = (ushort)((tileLocation * 16) + (line * 2) + vramBankOffset);
                        byte data1 = VRAM[dataAddress];
                        byte data2 = VRAM[dataAddress + 1];

                        for (int x = 0; x < 8; x++)
                        {
                            int colorBit = xFlip ? x : (7 - x);
                            int colorNum = (((data2 >> colorBit) & 1) << 1) | ((data1 >> colorBit) & 1);
                            if (colorNum == 0) continue;

                            uint finalColor;
                            if (IsGbc) finalColor = GetGbcColor(paletteIndex, colorNum, true);
                            else
                            {
                                int paletteVal = (dmgPalette >> (colorNum * 2)) & 3;
                                finalColor = Colors[paletteVal];
                            }

                            int drawY = (mapY + y) & 255;
                            int drawX = (mapX + x) & 255;
                            bgBuffer[(drawY * 256) + drawX] = finalColor;
                        }
                    }
                }
            }
            return bgBuffer;
        }

        public uint[] GetOamTexture()
        {
            uint[] oamBuffer = new uint[64 * 80];
            bool use8x16 = (LCDC & 0x04) != 0;
            int spriteHeight = use8x16 ? 16 : 8;

            for (int i = 0; i < 40; i++)
            {
                int oamIndex = i * 4;
                int tileLocation = OAM[oamIndex + 2];
                int attributes = OAM[oamIndex + 3];

                bool xFlip = (attributes & 0x20) != 0;
                bool yFlip = (attributes & 0x40) != 0;
                byte dmgPalette = ((attributes & 0x10) != 0) ? OBP1 : OBP0;

                int paletteIndex = 0;
                int vramBankOffset = 0;

                if (IsGbc)
                {
                    paletteIndex = attributes & 0x07;
                    vramBankOffset = ((attributes & 0x08) != 0) ? 0x2000 : 0x0000;
                }

                if (use8x16) tileLocation &= 0xFE;

                int gridX = (i % 8) * 8;
                int gridY = (i / 8) * 16;

                for (int y = 0; y < spriteHeight; y++)
                {
                    int line = yFlip ? (spriteHeight - 1 - y) : y;
                    ushort dataAddress = (ushort)((tileLocation * 16) + (line * 2) + vramBankOffset);
                    byte data1 = VRAM[dataAddress];
                    byte data2 = VRAM[dataAddress + 1];

                    for (int x = 0; x < 8; x++)
                    {
                        int colorBit = xFlip ? x : (7 - x);
                        int colorNum = (((data2 >> colorBit) & 1) << 1) | ((data1 >> colorBit) & 1);
                        if (colorNum == 0) continue;

                        uint finalColor;
                        if (IsGbc) finalColor = GetGbcColor(paletteIndex, colorNum, true);
                        else
                        {
                            int paletteVal = (dmgPalette >> (colorNum * 2)) & 3;
                            finalColor = Colors[paletteVal];
                        }

                        int drawX = gridX + x;
                        int drawY = gridY + y;
                        oamBuffer[(drawY * 64) + drawX] = finalColor;
                    }
                }
            }
            return oamBuffer;
        }

        public uint GetGbcColor(int paletteIndex, int colorIndex, bool isSprite)
        {
            byte[] ram = isSprite ? CgbObjPaletteRam : CgbBgPaletteRam;
            int address = (paletteIndex * 8) + (colorIndex * 2);

            byte low = ram[address];
            byte high = ram[address + 1];
            ushort color15 = (ushort)((high << 8) | low);

            int r = (color15 & 0x001F);
            int g = (color15 & 0x03E0) >> 5;
            int b = (color15 & 0x7C00) >> 10;

            r = (r << 3) | (r >> 2);
            g = (g << 3) | (g >> 2);
            b = (b << 3) | (b >> 2);

            return (uint)(0xFF000000 | (r << 16) | (g << 8) | b);
        }
    }
}