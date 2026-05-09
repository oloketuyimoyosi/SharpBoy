using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameboyTest
{
    public class PPU
    {
        // --- MEMORY REGISTERS ---
        public byte LCDC { get => io[0x40]; set => io[0x40] = value; } // 0xFF40
        public byte STAT { get => io[0x41]; set => io[0x41] = value; } // 0xFF41
        public byte LY { get => io[0x44]; set => io[0x44] = value; }   // 0xFF44
        public byte LYC { get => io[0x4B]; set => io[0x45] = value; }  // 0xFF45
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
        private bool statInterruptLine = false;

        private Action requestLcdInterrupt;
        private Action requestVBlankInterrupt;
        public uint[] FrameBuffer { get; private set; } = new uint[160 * 144];
        private byte[] oam { get; set; }
        private byte[] vram { get; set; }
        private byte[] io { get; set; }

        // A callback to tell your main SkiaSharp window: "The frame is ready, draw it!"
        public byte SCY { 
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
            get => io[0x4A]; // 0xFF4A
            set => io[0x4A] = value;
        }   // 0xFF4A
        public byte WX
        {
            get => io[0x4B]; // 0xFF4B
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
        public byte OBP1 { get => io[0x4B]; set => io[0x4B] = value; } // 0xFF49 (Sprite Palette 1)
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
        public void Tick(int cycles)
        {
            if (!IsLcdEnabled())
            {
                // HARDWARE QUIRK: When LCD is off, LY is 0, Mode is 0, and cycles reset.
                scanlineCounter = 0;
                
                LY = 0;
                STAT = (byte)((STAT & 0xFC) | 0x00);
                return;
            }

            scanlineCounter -= cycles;
            
            // If we finished a full horizontal line...
            if (scanlineCounter <=0)
            {
                scanlineCounter = SCANLINE_CYCLES;
                LY++;
                
                if (LY == 144)
                {
                    // We just entered V-Blank! 
                    requestVBlankInterrupt(); // Fire INT 0x40

                    // Tell SkiaSharp to draw the FrameBuffer to the screen!
                    // (This replaces your pygame.display.flip() logic)
                    requestFrameRender();
                }
                else if (LY > 153)
                {
                    LY = 0; // Reset back to the top of the screen
                }
                else if (LY < 144)
                {
                    DrawScanline();
                }
            }

            // Update the STAT register based on our current cycle and LY
            UpdateStatus();
        }

        // This is the direct C# translation of your SET_LCD_STATUS python method
        private void UpdateStatus()
        {
            byte currentMode = (byte)(STAT & 0x03);
            byte newMode = 0;
            bool requestStatInterrupt = false;

            // 1. Determine the New Mode
            if (LY >= 144)
            {
                newMode = 1; // V-Blank
            }
            else
            {
                if (scanlineCounter < MODE_2_BOUND)
                {
                    newMode = 2; // OAM Search
                }
                else if (scanlineCounter < MODE_3_BOUND)
                {
                    newMode = 3; // Pixel Transfer
                }
                else
                {
                    newMode = 0; // H-Blank
                }
            }

            // 2. Check for LY == LYC (Coincidence Flag)
            if (LY == LYC)
            {
                STAT = (byte)(STAT | 0x04); // Set LYC flag (Bit 2)

                // If LYC Interrupt is enabled (Bit 6)
                if ((STAT & 0x40) != 0)
                {
                    requestStatInterrupt = true;
                }
            }
            else
            {
                STAT = (byte)(STAT & ~0x04); // Clear LYC flag
            }

            // 3. Check Mode Interrupts
            if (newMode == 0 && (STAT & 0x08) != 0) requestStatInterrupt = true; // H-Blank Int
            if (newMode == 1 && (STAT & 0x10) != 0) requestStatInterrupt = true; // V-Blank Int
            if (newMode == 2 && (STAT & 0x20) != 0) requestStatInterrupt = true; // OAM Int

            // 4. Update the Mode bits in the STAT register
            if (currentMode != newMode)
            {
                STAT = (byte)((STAT & 0xFC) | newMode);
            }

            // 5. Fire the Interrupt with STAT Blocking! (Your 'triggered' array logic)
            // Only trigger if the internal hardware wire just transitioned from LOW to HIGH
            if (requestStatInterrupt)
            {
                if (!statInterruptLine)
                {
                    requestLcdInterrupt(); // Fire INT 0x48
                    statInterruptLine = true;
                }
            }
            else
            {
                // The wire went low, meaning it can trigger again in the future
                statInterruptLine = false;
            }
            if (currentMode == 3 && newMode == 0)
            {
                DrawScanline();
                throw new Exception("Scanline drawn!"); // For debugging, you can remove this later
            }
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

            bool windowEnabled = (LCDC & 0x20) != 0; // Bit 5

            // Check if the window is currently visible on this scanline
            if (windowEnabled && WY <= LY)
            {
                usingWindow = true;
            }

            // --- ULTRA-FAST SCANLINE LOOP ---
            for (int pixel = 0; pixel < 160; pixel++)
            {
                int xPos = pixel + SCX;
                int yPos = LY + SCY;
                ushort tileMapBase = bgMemory;

                // Are we drawing the window right now?
                if (usingWindow && pixel >= (WX - 7))
                {
                    xPos = pixel - (WX - 7);
                    yPos = windowLineCounter;
                    tileMapBase = windowMemory;
                }

                // Wrap coordinates to 256x256 grid
                xPos &= 255;
                yPos &= 255;

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
                int lineInTile = (yPos % 8) * 2;
                ushort dataAddress = (ushort)(tileDataAddress + (tileNum * 16) + lineInTile);

                // Read the two bytes representing the 8 pixels
                byte data1 = VRAM[dataAddress];
                byte data2 = VRAM[dataAddress + 1];

                // Extract the color bit
                int colorBit = 7 - (xPos % 8);
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
            int spritesDrawn = 0;

            // OAM contains 40 sprites (4 bytes each)
            // We loop forward, but the hardware gives priority to lower X coordinates.
            for (int i = 0; i < 160 && spritesDrawn < 10; i += 4)
            {
                int yPos = OAM[i] - 16;
                int xPos = OAM[i + 1] - 8;
                int tileLocation = OAM[i + 2];
                int attributes = OAM[i + 3];

                // Does this sprite intersect our current scanline?
                if (LY >= yPos && LY < (yPos + spriteHeight))
                {
                    spritesDrawn++;

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
}