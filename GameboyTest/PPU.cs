using GameboyTest.NewFolder;
using System.Diagnostics;
using System.Net;
using System.Timers;

namespace GameboyTest
{
    public class PPU
    {
        // --- MEMORY REGISTERS ---
        public byte LCDC { get => io[0x40]; set => io[0x40] = value; } // 0xFF40
        public byte STAT { get => io[0x41]; set => io[0x41] = value; } // 0xFF41
        public byte LY { get => io[0x44]; set => io[0x44] = value; }   // 0xFF44
        public byte LYC { get => io[0x45]; set => io[0x45] = value; }  // 0xFF45
                                                                       // --- VRAM & OAM (Super Fast Direct Access) ---


        // --- HARDWARE REGISTERS ---


        // --- SKIASHARP COLORS (AARRGGBB) ---
        // Your exact RGB colors: (202, 220, 159), (139, 172, 15), (48, 98, 48), (15, 56, 15)
        private readonly uint[] Colors = {
        0xFFCADC9F, // 0: Lightest
        0xFF8BAC0F, // 1: Light
        0xFF306230, // 2: Dark
        0xFF0F380F  // 3: Darkest
    };

        // To handle Sprite Priority properly, we need to remember what raw color 
        // the background just drew on the current scanline (0-3).
        private int[] scanlineRawColors = new int[160];

        // Hardware Quirk: The window has its own hidden internal line counter!
        private byte windowLineCounter = 0;
        // --- TIMING CONSTANTS ---
        private const int SCANLINE_CYCLES = 456;
        private const int MODE_2_BOUND = 80;
        private const int MODE_3_BOUND = 80 + 172; // 252

        // --- INTERNAL STATE ---
        private int scanlineCounter = 456;
        // --- GBC PALETTE RAM ---
        // 64 bytes each (8 palettes * 4 colors * 2 bytes per color)
        public byte[] CgbBgPaletteRam { get; private set; } = new byte[64];
        public byte[] CgbObjPaletteRam { get; private set; } = new byte[64];

        // Tells the PPU if it should render in full Color mode or classic DMG mode
        public bool IsGbc = false;

        // NEW: We need to remember if the background asked for priority on this specific pixel
        private bool[] scanlineBgPriority = new bool[160];
        // Palette Index Registers
        public byte BCPS { get => io[0x68]; set => io[0x68] = value; } // 0xFF68
        public byte OCPS { get => io[0x6A]; set => io[0x6A] = value; } // 0xFF6A
        // This replaces your 'triggered' array for accurate STAT blocking!
        private System.IO.StreamWriter debugLog;
        private bool tick = false;
        private bool ppu_mode_on_off = false;
        private Action requestLcdInterrupt;
        private Action requestVBlankInterrupt;
        // SkiaSharp reads from this one (The Front Buffer)
        // Hardware Quirk: The window has its own hidden internal line counter!


        // NEW: The Window Y Trigger Latch
        private bool windowYTriggered = false;

        // The PPU draws to this one secretly (The Back Buffer)

        public uint[] DisplayBuffer { get; private set; } = new uint[160 * 144];
        // A lock to prevent them from crashing into each other
        public readonly object BufferLock = new object();
        public uint[] FrameBuffer { get; private set; } = new uint[160 * 144];
        private byte[] oam { get; set; }
        private byte[] vram { get; set; }
        private byte[] io { get; set; }
        private bool ly_check_triggered { get; set; } = false;
        private bool? lcd_on_off = null;
        private bool ly_compare_stored = false;
        private bool ly_match_stored = false;

        private bool ly_match = false;
        private struct SpriteData
        {
            public int Y;
            public int X;
            public int Tile;
            public int Attributes;
            public int OamIndex;
        }
        private bool isLine153 = false;
        // A callback to tell your main SkiaSharp window: "The frame is ready, draw it!"
        public byte SCY
        {
            get => io[0x42]; // 0xFF42
            set => io[0x42] = value;
        }
        public byte SCX
        {
            get => io[0x43];
            set => io[0x43] = value;
        }// 0xFF43
        public byte WY
        {
            get => (byte)(io[0x4A]); // 0xFF4A
            set => io[0x4A] = value;
        }   // 0xFF4A
        public byte WX
        {
            get => (byte)(io[0x4B] - 7); // 0xFF4B
            set => io[0x4B] = value;
        }
        public byte BGP
        {
            get => io[0x47]; // 0xFF47
            set => io[0x47] = value;
        }     // 0xFF47 (Background Palette)
        public byte OBP0
        {
            get => io[0x48]; // 0xFF4B
            set => io[0x48] = value;
        }   // 0xFF48 (Sprite Palette 0)
        public byte[] VRAM { get; private set; } = new byte[0x4000];
        public byte[] OAM { get; private set; } = new byte[160];

        // Register 0xFF4F (VBK) - VRAM Bank. Bits 1-7 are always 1.
        public byte VBK
        {
            get => (byte)(io[0x4F] | 0xFE);
            set => io[0x4F] = (byte)(value | 0xFE);
        }
        public byte OBP1 { get => io[0x49]; set => io[0x49] = value; } // 0xFF49 (Sprite Palette 1)
        private Action requestFrameRender;
        private Action performHdmaCallback;
        public PPU(Action lcdInterrupt, Action vBlankInterrupt, Action renderCallback, byte[] io, Action hdmaCallback)
        {
            this.requestLcdInterrupt = lcdInterrupt;
            this.requestVBlankInterrupt = vBlankInterrupt;
            this.requestFrameRender = renderCallback;
            this.io = io;
            this.performHdmaCallback = hdmaCallback;



        }
        // Helper to check if the LCD is currently turned on (Bit 7 of LCDC)

        public byte ReadBgPaletteData() => CgbBgPaletteRam[BCPS & 0x3F];

        public void WriteBgPaletteData(byte value)
        {
            CgbBgPaletteRam[BCPS & 0x3F] = value;

            // Auto-Increment Flag (Bit 7)
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

            // Auto-Increment Flag (Bit 7)
            if ((OCPS & 0x80) != 0)
            {
                byte nextIndex = (byte)((OCPS & 0x3F) + 1);
                OCPS = (byte)(0x80 | (nextIndex & 0x3F));
            }
        }
        private bool IsLcdEnabled()
        {
            return (LCDC & 0x80) != 0;
        }
        public byte ReadVram(ushort address)
        {
            int offset = address - 0x8000;
            int bank = VBK & 0x01; // Only look at Bit 0
            return VRAM[(bank * 0x2000) + offset];
        }

        public void WriteVram(ushort address, byte value)
        {
            int offset = address - 0x8000;
            int bank = VBK & 0x01;
            VRAM[(bank * 0x2000) + offset] = value;
        }
        // Called by the CPU every time it ticks!
        public void Tick(int cycles, ushort PC, bool halted, byte ie, bool IME, bool Interrupt_on_Line)
        {
            if (IsLcdEnabled())
            {
                scanlineCounter -= cycles;

                // --- THE LINE 153 QUIRK ---
                // If we are on line 153, and 4 T-cycles have passed, LY instantly drops to 0!
                if (isLine153 && scanlineCounter <= (SCANLINE_CYCLES - 4))
                {
                    LY = 0;
                }
            }

            // If we finished a full horizontal line...
            if ((scanlineCounter <= 0))
            {
                scanlineCounter += SCANLINE_CYCLES; // Use += to perfectly preserve leftover cycles
                tick = false;

                if (isLine153)
                {
                    // We just finished the remaining 452 cycles of line 153. Officially move to line 0.
                    isLine153 = false;
                    LY = 0;
                    windowYTriggered = false;
                }
                else
                {
                    LY++;

                    if (LY == 144)
                    {
                        lock (BufferLock)
                        {
                            Array.Copy(FrameBuffer, DisplayBuffer, FrameBuffer.Length);
                        }
                        if (IsLcdEnabled()) requestVBlankInterrupt();
                        requestFrameRender();

                    }
                    else if (LY == 153)
                    {
                        isLine153 = true; // Trigger the quirk for the next loop!
                    }

                    if (LY > 144)
                    {
                        windowLineCounter = 0;
                    }
                }
                if (LY == WY)
                {
                    windowYTriggered = true;
                }
            }

            UpdateStatus(PC, halted, ie, IME, Interrupt_on_Line);

            if (!tick)
            {
                // Safety check: Don't draw if we are executing the fake LY=0 quirk
                if (!isLine153 && LY < 144 && (calculatemode(scanlineCounter, LY, IsLcdEnabled(), PC) == 0))
                {
                    DrawScanline();
                    tick = true;
                }
            }
        }

        // This is the direct C# translation of your SET_LCD_STATUS python method
        private void UpdateStatus(ushort PC, bool halted, byte ie, bool IME, bool Interrupt_on_Line)
        {


            byte current_mode = (byte)(STAT & 0x3);
            bool requestStatInterrupt = false;
            bool requestLyInterrupt = false;


            if (!IsLcdEnabled())
            {
                scanlineCounter = SCANLINE_CYCLES;
                LY = 0;


            }





            int new_mode = calculatemode(scanlineCounter, LY, IsLcdEnabled(),PC);





            if (new_mode == 0 && current_mode != 0)
            {
                if (IsGbc) performHdmaCallback(); // Copy 16 bytes of data!
            }

            ly_match = (LY == LYC);
            if (IsLcdEnabled())
            {
                ly_match_stored = ly_match;
            }
            else
            {
                ly_match = ly_match_stored;
            }
            if ((lcd_on_off == false) && (IsLcdEnabled() == true)) //Will change depending on the outcome of lcd_on_off test
            {

                ly_match = ly_match_stored;


                ppu_mode_on_off = true;
                lcd_on_off = true;
            }
            else
            {
                ppu_mode_on_off = false;
            }


            int new_status = updateStatus(STAT, new_mode);
            new_status |= 0x80;


            if (IsLcdEnabled() == false)
            {
                new_status &= 0xFC;
                new_mode = 0;
            }

            if (new_mode == 0 && ((new_status & 0x08) != 0)) requestStatInterrupt = true; // H-Blank Int
            if (new_mode == 1 && ((new_status & 0x10) != 0)) requestStatInterrupt = true; // V-Blank Int
            if (new_mode == 2 && ((new_status & 0x20) != 0)) requestStatInterrupt = true; // OAM Int


            // 4. Update the Mode bits in the STAT register
            if (ly_match && ((new_status & 0x40) != 0))
            {

                //throw new Exception($"STAT Interrupt Triggered! LY={LY}, LYC={LYC}, Mode={(STAT & 0x03)}, STAT={Convert.ToString(STAT, 2).PadLeft(8, '0')}");

                requestLyInterrupt = true;

                //requestLcdInterrupt();
                //Debug.WriteLine($"1. LY=LYC Interrupt Condition Met! LY={LY}, LYC={LYC}, Mode={(STAT & 0x03)}, STAT={Convert.ToString(STAT, 2).PadLeft(8, '0')}");
            }





            // 5. Fire the Interrupt with STAT Blocking! (Your 'triggered' array logic)
            // Only trigger if the internal hardware wire just transitioned from LOW to HIGH
            if ((ly_check_triggered == false) && ((requestLyInterrupt || requestStatInterrupt)))
            {

                /*if (!statInterruptLine)
                {
                    requestLcdInterrupt(); // Fire INT 0x48
                    statInterruptLine = true;
                }*/

                    requestLcdInterrupt();

                


                //throw new Exception($"STAT Interrupt Triggered! LY={LY}, LYC={LYC}, Mode={(STAT & 0x03)}, STAT={Convert.ToString(STAT, 2).PadLeft(8, '0')}");

            }

            // The wire went low, meaning it can trigger again in the future
            ly_check_triggered = requestLyInterrupt || requestStatInterrupt;



            STAT = (byte)new_status;
            lcd_on_off = IsLcdEnabled();

        }

        private int calculatemode(int scanline_cycles, int current_line, bool lcd_enabled, ushort PC)
        {
            if (lcd_enabled)
            {
                // THE FIX: Even though current_line (LY) is 0, force Mode 1 if the quirk is active!
                if (current_line >= 144 || isLine153)
                {
                    return 0x1;
                }
                else if (scanline_cycles >= (SCANLINE_CYCLES - MODE_2_BOUND))
                {
                    return 0x2;
                }
                else if (scanline_cycles >= (SCANLINE_CYCLES - MODE_3_BOUND))
                {
                    return 0x3;
                }
                else
                {
                    return 0x0;
                }
            }
            return 0x0;
        }
        private int updateStatus(int status, int mode)
        {
            status = (status & 0xFC) | (mode);
            
            if (ly_match)
            {
                status |= 0x4;
            }
            else
            {
                status &= 0xFB;
            }

            return status;
        }
        private void DrawScanline()
        {
            // Bit 0: BG Enable, Bit 1: Sprite Enable
            if (IsGbc || (LCDC & 0x01) != 0)
            {
                RenderTiles();
            }

            // Bit 1: Sprite Enable
            if ((LCDC & 0x02) != 0)
            {
                RenderSprites();
            }
        }

        private void RenderTiles()
        {
            bool usingWindow = false;

            // Pre-compute control flags (Exactly like your Python code)


            bool tileDataSigned = (LCDC & 0x10) == 0; // Bit 4
            ushort tileDataAddress = (ushort)(tileDataSigned ? 0x0800 : 0x0000); // Offset into VRAM array

            ushort bgMemory = (ushort)(((LCDC & 0x08) != 0) ? 0x1C00 : 0x1800); // Bit 3
            ushort windowMemory = (ushort)(((LCDC & 0x40) != 0) ? 0x1C00 : 0x1800); // Bit 6

            // Bit 5

            // Check if the window is currently visible on this scanline
            

            // --- ULTRA-FAST SCANLINE LOOP ---
            for (int pixel = 0; pixel < 160; pixel++)
            {
                bool windowEnabled = (LCDC & 0x20) != 0;
                int xPos = pixel + SCX;
                int yPos = LY + SCY;
                ushort tileMapBase = bgMemory;
                xPos &= 255;
                yPos &= 255;

                // --- OLD CODE ---
                // if (windowEnabled && LY >= WY && pixel > WX - 1)

                // --- NEW CODE ---
                if (windowEnabled && windowYTriggered && pixel > WX - 1)
                {
                    usingWindow = true;
                }
                // Are we drawing the window right now?
                if (usingWindow)
                {
                    xPos = pixel - WX;
                    yPos = windowLineCounter;
                    tileMapBase = windowMemory;

                }

                // Wrap coordinates to 256x256 grid


                // Which of the 32x32 tiles are we in?
                int tileCol = xPos / 8;
                int tileRow = yPos / 8;

                // 1. Fetch the Tile ID from VRAM Bank 0
                ushort tileAddress = (ushort)(tileMapBase + (tileRow * 32) + tileCol);
                int tileNum = VRAM[tileAddress];

                if (tileDataSigned)
                {
                    tileNum = (sbyte)tileNum;
                    tileNum += 128;
                }

                // 2. --- GBC ATTRIBUTE FETCHING ---
                int vramBankOffset = 0;
                int paletteIndex = 0;
                bool xFlip = false;
                bool yFlip = false;
                bool bgPriority = false;

                if (IsGbc)
                {
                    // The attributes live at the exact same address, just shifted into Bank 1 (+0x2000)
                    byte attributes = VRAM[tileAddress + 0x2000];
                    paletteIndex = attributes & 0x07;                            // Bits 0-2: Palette Number
                    vramBankOffset = ((attributes & 0x08) != 0) ? 0x2000 : 0x0000; // Bit 3: VRAM Bank
                    xFlip = (attributes & 0x20) != 0;                            // Bit 5: X Flip
                    yFlip = (attributes & 0x40) != 0;                            // Bit 6: Y Flip
                    bgPriority = (attributes & 0x80) != 0;                       // Bit 7: BG-to-OAM Priority
                }

                // 3. Handle Y-Flip
                int lineInTile = yPos & 7;
                if (yFlip) lineInTile = 7 - lineInTile; // Read from the bottom up!

                ushort dataAddress = (ushort)(tileDataAddress + (tileNum * 16) + (lineInTile * 2) + vramBankOffset);
                byte data1 = VRAM[dataAddress];
                byte data2 = VRAM[dataAddress + 1];

                // 4. Handle X-Flip
                int tilePixelX = xPos & 7;
                if (xFlip) tilePixelX = 7 - tilePixelX; // Read from right to left!
                int colorBit = 7 - tilePixelX;

                // 5. Extract the 2-bit color number
                int colorNum = (((data2 >> colorBit) & 1) << 1) | ((data1 >> colorBit) & 1);

                // Store raw color and priority for the sprite renderer to check later
                scanlineRawColors[pixel] = colorNum;
                scanlineBgPriority[pixel] = bgPriority;

                // 6. --- RENDER THE FINAL PIXEL ---
                if (IsGbc)
                {
                    // Use the new 15-bit color hardware!
                    FrameBuffer[LY * 160 + pixel] = GetGbcColor(paletteIndex, colorNum, false);
                }
                else
                {
                    // Fall back to classic monochrome
                    int paletteVal = (BGP >> (colorNum * 2)) & 3;
                    FrameBuffer[LY * 160 + pixel] = Colors[paletteVal];
                }
            }

            // If the window was drawn on this scanline, increment its hidden counter
            if (usingWindow) { windowLineCounter++; }
        }

        private void RenderSprites()
        {
            bool use8x16 = (LCDC & 0x04) != 0; // Bit 2
            int spriteHeight = use8x16 ? 16 : 8;
            bool masterPriority = (LCDC & 0x01) != 0;
            // Step 1: Collect up to 10 sprites that intersect this scanline
            var sprites = new List<SpriteData>(10);

            for (int i = 0; i < 160 && sprites.Count < 10; i += 4)
            {
                int yPos = OAM[i] - 16;

                // Does this sprite intersect our current scanline?
                if (LY >= yPos && LY < (yPos + spriteHeight))
                {
                    sprites.Add(new SpriteData
                    {
                        Y = yPos,
                        X = OAM[i + 1] - 8,
                        Tile = OAM[i + 2],
                        Attributes = OAM[i + 3],
                        OamIndex = i
                    });
                }
            }

            // Exit early if no sprites are on this line
            if (sprites.Count == 0) return;

            // Step 2: Sort the sprites by Priority.
            // Game Boy priority: Lowest X coordinate draws ON TOP. If X is tied, Lowest OAM index draws ON TOP.
            // By sorting DESCENDING, we draw the lowest priority sprites first, letting the higher priority 
            // sprites safely overwrite them at the end of the loop (just like the Python reverse() logic).
            // Step 2: Sort the sprites by Priority.
            sprites.Sort((a, b) => {
                if (IsGbc)
                {
                    // GBC strictly prioritizes based on OAM index (Lower index = drawn later = ON TOP)
                    return b.OamIndex.CompareTo(a.OamIndex);
                }
                else
                {
                    // DMG prioritizes lowest X coordinate first. If tied, it falls back to OAM index.
                    int xCmp = b.X.CompareTo(a.X);
                    if (xCmp != 0) return xCmp;
                    return b.OamIndex.CompareTo(a.OamIndex);
                }
            });
            // Step 3: Draw the sorted sprites
            foreach (var sprite in sprites)
            {
                int yPos = sprite.Y;
                int xPos = sprite.X;
                int tileLocation = sprite.Tile;
                int attributes = sprite.Attributes;

                bool yFlip = (attributes & 0x40) != 0;
                bool xFlip = (attributes & 0x20) != 0;
                bool objBehindBg = (attributes & 0x80) != 0; // Priority

                // --- GBC ATTRIBUTE FETCHING ---
                int paletteIndex = 0;
                int vramBankOffset = 0;
                byte dmgPalette = ((attributes & 0x10) != 0) ? OBP1 : OBP0;

                if (IsGbc)
                {
                    paletteIndex = attributes & 0x07;                              // Bits 0-2: Palette Number
                    vramBankOffset = ((attributes & 0x08) != 0) ? 0x2000 : 0x0000; // Bit 3: VRAM Bank
                }

                // Determine which line of the sprite we are drawing
                int line = LY - yPos;
                if (yFlip) line = (spriteHeight - 1) - line;

                if (use8x16) tileLocation &= 0xFE;

                // Grab the data, ensuring we read from the correct VRAM bank!
                ushort dataAddress = (ushort)((tileLocation * 16) + (line * 2) + vramBankOffset);
                byte data1 = VRAM[dataAddress];
                byte data2 = VRAM[dataAddress + 1];

                // Draw the 8 pixels
                for (int tilePixel = 0; tilePixel < 8; tilePixel++)
                {
                    int colorBit = xFlip ? tilePixel : (7 - tilePixel);
                    int colorNum = (((data2 >> colorBit) & 1) << 1) | ((data1 >> colorBit) & 1);

                    // Color 0 is always transparent for sprites!
                    if (colorNum == 0) continue;

                    int pixelX = xPos + tilePixel;
                    if (pixelX < 0 || pixelX > 159) continue;

                    // --- GBC SPRITE PRIORITY LOGIC ---
                    // The GBC has slightly more complex priority. If the Background specifically 
                    // asked for priority (Bit 7 of BG Attributes), the sprite is ALWAYS hidden 
                    // behind it, unless the background pixel is transparent (Color 0).
                    // If we are in CGB mode and Master Priority is 0, sprites ALWAYS draw on top.
                    // We only respect priority flags if we are in DMG mode OR Master Priority is 1.
                    if (!IsGbc || masterPriority)
                    {
                        // The Background specifically asked for priority (CGB only)
                        if (IsGbc && scanlineBgPriority[pixelX] && scanlineRawColors[pixelX] != 0) continue;

                        // The Sprite specifically asked to go behind the background
                        if (objBehindBg && scanlineRawColors[pixelX] != 0) continue;
                    }
                    // --- RENDER THE FINAL PIXEL ---
                    if (IsGbc)
                    {
                        FrameBuffer[LY * 160 + pixelX] = GetGbcColor(paletteIndex, colorNum, true);
                    }
                    else
                    {
                        int paletteVal = (dmgPalette >> (colorNum * 2)) & 3;
                        FrameBuffer[LY * 160 + pixelX] = Colors[paletteVal];
                    }
                }
            }

        }
        // Add paletteChoice parameter: 0 = BGP, 1 = OBP0, 2 = OBP1
        // Expand to 256x256 buffer!
        public uint[] GetVramTexture(int paletteChoice = 0, int gbcPaletteIndex = 0)
        {
            // 256 pixels wide x 256 pixels high (Fits all 1024 tiles across both banks)
            uint[] vramBuffer = new uint[256 * 256];

            byte targetPalette = BGP;
            bool isSpritePalette = false;
            if (paletteChoice == 1) { targetPalette = OBP0; isSpritePalette = true; }
            if (paletteChoice == 2) { targetPalette = OBP1; isSpritePalette = true; }

            // Loop through 1024 tiles. Tiles 0-511 are Bank 0, Tiles 512-1023 are Bank 1.
            for (int tileIndex = 0; tileIndex < 1024; tileIndex++)
            {
                // Calculate which VRAM bank to read from
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
                        if (IsGbc)
                        {
                            // Use the 15-bit hardware colors
                            finalColor = GetGbcColor(gbcPaletteIndex, colorNum, isSpritePalette);
                        }
                        else
                        {
                            // Fall back to DMG green palettes
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

            // --- 1. RENDER BACKGROUND MAP ---
            for (int tileY = 0; tileY < 32; tileY++)
            {
                for (int tileX = 0; tileX < 32; tileX++)
                {
                    int mapIndex = mapBase + (tileY * 32) + tileX;
                    int tileNum = VRAM[mapIndex];

                    if (tileDataSigned)
                    {
                        tileNum = (sbyte)tileNum;
                        tileNum += 128;
                    }

                    // GBC Attributes
                    int vramBankOffset = 0;
                    int paletteIndex = 0;
                    bool xFlip = false;
                    bool yFlip = false;

                    if (IsGbc)
                    {
                        byte attributes = VRAM[mapIndex + 0x2000]; // Attributes are in Bank 1
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
                            if (IsGbc)
                            {
                                finalColor = GetGbcColor(paletteIndex, colorNum, false);
                            }
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

            // --- 2. RENDER OVERLAY SPRITES ---
            if (showSprites)
            {
                bool use8x16 = (LCDC & 0x04) != 0;
                int spriteHeight = use8x16 ? 16 : 8;

                for (int i = 39; i >= 0; i--) // Reverse order for basic visual priority
                {
                    int oamIndex = i * 4;
                    int screenY = OAM[oamIndex] - 16;
                    int screenX = OAM[oamIndex + 1] - 8;
                    int tileLocation = OAM[oamIndex + 2];
                    int attributes = OAM[oamIndex + 3];

                    // Project screen coordinates to absolute Map coordinates using scroll
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

                            if (colorNum == 0) continue; // Skip transparent pixels

                            uint finalColor;
                            if (IsGbc)
                            {
                                finalColor = GetGbcColor(paletteIndex, colorNum, true);
                            }
                            else
                            {
                                int paletteVal = (dmgPalette >> (colorNum * 2)) & 3;
                                finalColor = Colors[paletteVal];
                            }

                            // Wrap rendering around the edges of the 256x256 map
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
            // Width: 8 cols * 8 pixels = 64
            // Height: 5 rows * 16 pixels = 80
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
                        if (IsGbc)
                        {
                            finalColor = GetGbcColor(paletteIndex, colorNum, true);
                        }
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

        //GBC PALETTE READING HELPER - Given a palette index (0-7), color index (0-3), and whether it's a sprite or background palette, return the final 32-bit ARGB color for SkiaSharp!
        public uint GetGbcColor(int paletteIndex, int colorIndex, bool isSprite)
        {
            byte[] ram = isSprite ? CgbObjPaletteRam : CgbBgPaletteRam;

            // Each color takes 2 bytes. 4 colors per palette.
            int address = (paletteIndex * 8) + (colorIndex * 2);

            byte low = ram[address];
            byte high = ram[address + 1];
            ushort color15 = (ushort)((high << 8) | low);

            // Extract the 5-bit RGB channels
            int r = (color15 & 0x001F);
            int g = (color15 & 0x03E0) >> 5;
            int b = (color15 & 0x7C00) >> 10;

            // Scale the 5-bit channel (0-31) up to an 8-bit channel (0-255).
            // Shifting left by 3 gets us 90% of the way there, and ORing it with 
            // a shift right by 2 accurately fills in the lowest bits so true white is bright!
            r = (r << 3) | (r >> 2);
            g = (g << 3) | (g >> 2);
            b = (b << 3) | (b >> 2);

            // Return as AARRGGBB for SkiaSharp (Fully Opaque)
            return (uint)(0xFF000000 | (r << 16) | (g << 8) | b);
        }
    }
}