using System.Diagnostics;
using System.Net;

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
        public byte[] VRAM { get; private set; } = new byte[8192]; // 0x8000 - 0x9FFF
        public byte[] OAM { get; private set; } = new byte[160];   // 0xFE00 - 0xFE9F

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
        private int windowLineCounter = 0;
        // --- TIMING CONSTANTS ---
        private const int SCANLINE_CYCLES = 456;
        private const int MODE_2_BOUND = 80;
        private const int MODE_3_BOUND = 80 + 172; // 252

        // --- INTERNAL STATE ---
        private int scanlineCounter = 456;

        // This replaces your 'triggered' array for accurate STAT blocking!

        private bool tick = false;
        private bool ppu_mode_on_off = false;
        private Action requestLcdInterrupt;
        private Action requestVBlankInterrupt;
        public uint[] FrameBuffer { get; private set; } = new uint[160 * 144];
        private byte[] oam { get; set; }
        private byte[] vram { get; set; }
        private byte[] io { get; set; }
        private bool ly_check_triggered { get; set; } = false;
        private bool lcd_on_off = false;
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
            get => (byte)(io[0x4A] - 1); // 0xFF4A
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
        public byte OBP1 { get => io[0x49]; set => io[0x49] = value; } // 0xFF49 (Sprite Palette 1)
        private Action requestFrameRender;
        public PPU(Action lcdInterrupt, Action vBlankInterrupt, Action renderCallback, byte[] io, byte[] vram, byte[] oam)
        {
            this.requestLcdInterrupt = lcdInterrupt;
            this.requestVBlankInterrupt = vBlankInterrupt;
            this.requestFrameRender = renderCallback;
            this.io = io;
            this.vram = vram;
            this.oam = oam;
        }
        // Helper to check if the LCD is currently turned on (Bit 7 of LCDC)
        private bool IsLcdEnabled()
        {
            return (LCDC & 0x80) != 0;
        } 

        // Called by the CPU every time it ticks!
        public void Tick(int cycles, ushort PC, bool halted, byte ie,bool IME,bool Interrupt_on_Line)
        {

            if (IsLcdEnabled())
            {

                scanlineCounter -= cycles;
            }


            // If we finished a full horizontal line...
            if ((scanlineCounter <= 0)&& (tick == false))
            {
                if (LY < 144 && (calculatemode(scanlineCounter,LY,IsLcdEnabled(),PC) == 0))
                {
                    DrawScanline();
                    tick = true;
                }
                scanlineCounter = SCANLINE_CYCLES;
                LY++;
                tick = false;

                if (LY == 144)
                {
                    if (IsLcdEnabled())
                    {
                        requestVBlankInterrupt();
                    }

                    requestFrameRender();
                }
                else if (LY > 153)
                {
                    
                    LY = 0; // Reset back to the top of the screen
                }
                else if (LY > 144)
                {
                    windowLineCounter = 0;
                }

            }

            UpdateStatus(PC,halted,ie,IME,Interrupt_on_Line);


            // Update the STAT register based on our current cycle and LY

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




            


            ly_match = (LY == LYC);
            if (IsLcdEnabled())
            {
                ly_match_stored = ly_match;
            }
            else
            {
                ly_match = ly_match_stored;
            }
            if ((lcd_on_off == false)&&(IsLcdEnabled() == true))
            {

                    ly_match = ly_match_stored;


                    ppu_mode_on_off = true;
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
            if ((ly_check_triggered == false) && ((requestLyInterrupt || requestStatInterrupt) == true) && (Interrupt_on_Line == false))
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



            //Debug.WriteLine($"2.  LY={LY}, LYC={LYC}, Mode={(STAT & 0x03)}, STAT={STAT.ToString("X8")},Ly_match={ly_match},scanline= {scanlineCounter}, ly_check_triggered = {ly_check_triggered} PC {PC} halted {halted} ie {ie} if {io[0xF]} IME {IME} interrupt {(ly_check_triggered == false && ((requestLyInterrupt || requestStatInterrupt) == true))}");


            STAT = (byte)new_status;
            lcd_on_off = IsLcdEnabled();

        }

        private int calculatemode(int scanline_cycles, int current_line, bool lcd_enabled,ushort PC)
        {
            if (lcd_enabled)
            {
                if((ppu_mode_on_off)&&(LY == 0))
                {
                    
                    if (scanline_cycles >= (SCANLINE_CYCLES - 204))
                    {
                        return 0x0;
                    }else if (scanline_cycles >= (SCANLINE_CYCLES - MODE_3_BOUND))
                    {
                        return 0x3;
                    }
                    else
                    {
                        return 0;
                    }
                }
                else
                {
                    ppu_mode_on_off = false;

                        if (current_line >= 144)
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
                            //
                            return 0x0;



                        }

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
            if ((LCDC & 0x01) != 0) RenderTiles();
            if ((LCDC & 0x02) != 0) RenderSprites();
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

                if (windowEnabled && LY > WY && pixel > WX - 1)
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

                // Fetch the Tile ID from VRAM
                ushort tileAddress = (ushort)(tileMapBase + (tileRow * 32) + tileCol);
                int tileNum = VRAM[tileAddress];

                // Adjust for signed tile numbers (-128 to 127)
                if (tileDataSigned)
                {
                    tileNum = (sbyte)tileNum;

                    tileNum += 128;
                }

                // Find the exact line in the tile (16 bytes per tile, 2 bytes per row)
                int lineInTile = (yPos & 7) * 2;
                ushort dataAddress = (ushort)(tileDataAddress + (tileNum * 16) + lineInTile);

                // Read the two bytes representing the 8 pixels
                byte data1 = VRAM[dataAddress];
                byte data2 = VRAM[dataAddress + 1];

                // Extract the color bit
                int colorBit = 7 - (xPos & 7);
                int colorNum = (((data2 >> colorBit) & 1) << 1) | ((data1 >> colorBit) & 1);

                // Store raw color for sprite priority checks later
                scanlineRawColors[pixel] = colorNum;

                // Resolve through the Background Palette (BGP)
                int paletteVal = (BGP >> (colorNum * 2)) & 3;

                // Push directly to the SkiaSharp FrameBuffer!
                FrameBuffer[LY * 160 + pixel] = Colors[paletteVal];
            }

            // If the window was drawn on this scanline, increment its hidden counter
            if (usingWindow) windowLineCounter++;
        }

        private void RenderSprites()
        {
            bool use8x16 = (LCDC & 0x04) != 0; // Bit 2
            int spriteHeight = use8x16 ? 16 : 8;

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
            sprites.Sort((a, b) => {
                int xCmp = b.X.CompareTo(a.X);
                if (xCmp != 0) return xCmp;
                return b.OamIndex.CompareTo(a.OamIndex);
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
                bool usePalette1 = (attributes & 0x10) != 0;
                bool objBehindBg = (attributes & 0x80) != 0; // Priority

                byte palette = usePalette1 ? OBP1 : OBP0;

                // Determine which line of the sprite we are drawing
                int line = LY - yPos;
                if (yFlip)
                {
                    line = (spriteHeight - 1) - line;
                }

                if (use8x16)
                {
                    // 8x16 sprites ignore the bottom bit of the tile index
                    tileLocation &= 0xFE;
                }

                // Get memory address
                ushort dataAddress = (ushort)((tileLocation * 16) + (line * 2));
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

                    // Skip if off-screen
                    if (pixelX < 0 || pixelX > 159) continue;

                    // Sprite Priority Logic:
                    // If objBehindBg is true, the sprite ONLY draws if the background color was 0 (transparent).
                    if (objBehindBg && scanlineRawColors[pixelX] != 0) continue;

                    // Map through palette and draw!
                    int paletteVal = (palette >> (colorNum * 2)) & 3;
                    FrameBuffer[LY * 160 + pixelX] = Colors[paletteVal];
                }
            }
        }
    }
}