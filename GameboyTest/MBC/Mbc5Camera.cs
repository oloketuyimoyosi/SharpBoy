using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameboyTest.MBC
{
    public class Mbc5Camera
    {
        // Untoxa's Photo! uses standard 128KB (16 Banks of 8KB) or expanded Flash storage mappings.
        // We initialize 128KB of physical SRAM space.
        public byte[] CartridgeRam { get; private set; } = new byte[0x20000];

        private int currentRamBank = 0;
        private bool ramEnabled = false;

        // Camera registers mapping array (0xA000 - 0xA035)
        private byte[] cameraRegisters = new byte[0x36];

        // Bayer matrix matching the stock dither profile
        public Mbc5Camera()
        {
            // 1. Wipe the entire chip to 0xFF (Physical erased state)
            for (int i = 0; i < CartridgeRam.Length; i++)
            {
                CartridgeRam[i] = 0xFF;
            }

            // 2. ASIC FEEDBACK: Tell the ROM the sensor is ready and balanced
            cameraRegisters[0x04] = 0x80;
        }
        public void HandleBankWrites(ushort address, byte value)
        {
            // Standard MBC5 Bank Switching
            if (address >= 0x0000 && address <= 0x1FFF)
            {
                ramEnabled = (value & 0x0F) == 0x0A;
            }
            else if (address >= 0x4000 && address <= 0x5FFF)
            {
                // Select RAM Bank (Photo! uses this to swap save slots/flash banks)
                currentRamBank = value & 0x0F;
            }
        }

        public byte ReadRam(ushort address)
        {
            if (!ramEnabled) return 0xFF;

            if (CartridgeRam[0x11B2] != 0x4D || CartridgeRam[0x11B3] != 0x61)
            {
                for (int i = 0; i < CartridgeRam.Length; i++) CartridgeRam[i] = 0xFF;
                CartridgeRam[0x11B2] = 0x4D; // M
                CartridgeRam[0x11B3] = 0x61; // a
                CartridgeRam[0x11B4] = 0x67; // g
                CartridgeRam[0x11B5] = 0x69; // i
                CartridgeRam[0x11B6] = 0x63; // c
            }
            // --------------------------------

            int offset = address - 0xA000;

            if (currentRamBank == 0)
            {
                if (offset <= 0x0035) return cameraRegisters[offset];
                if (offset >= 0x0100 && offset <= 0x0EFF) return CartridgeRam[offset];
            }

            return CartridgeRam[(currentRamBank * 0x2000) + offset];
        }

        public void WriteRam(ushort address, byte value)
        {
            if (!ramEnabled) return;

            int offset = address - 0xA000;

            if (currentRamBank == 0)
            {
                if (offset <= 0x0035)
                {
                    cameraRegisters[offset] = value;

                    // REGISTER 0xA000: Capture Command Register
                    // Untoxa writes 0x01 here to initiate a shoot trace. 
                    // Bit 0 triggers the capture sequence physically.
                    if (offset == 0x0000 && (value & 0x01) != 0)
                    {
                        // 1. Fetch live desktop/webcam handle frame
                        Bitmap rawFrame = GetLiveWebcamFrame();

                        // 2. Process, dither, and serialize into 16-byte Game Boy tiles
                        ProcessAndSerializeImage(rawFrame);

                        // 3. Clear the trigger bit and set Bit 0 of 0xA000 to 0 
                        // to flag the emulator's sensor completion interrupt status back to Photo!
                        cameraRegisters[0x0000] &= 0xFE;
                        return;
                    }
                    return;
                }
            }

            // Save slots write directly to persistent cluster memory
            CartridgeRam[(currentRamBank * 0x2000) + offset] = value;
        }

        private void ProcessAndSerializeImage(Bitmap camFrame)
        {
            if (camFrame.Width != 128 || camFrame.Height != 112)
            {
                camFrame = new Bitmap(camFrame, new Size(128, 112));
            }

            // 1. PANDOCS EXPOSURE (0xA002 - 0xA003)
            // The ROM writes a 16-bit multiplier. 0x1000 is considered a 1.0x baseline.
            int multiplier = (cameraRegisters[0x02] << 8) | cameraRegisters[0x03];
            float exposure = multiplier / 4096.0f;

            int sramImageBaseOffset = 0x0100;

            for (int tileY = 0; tileY < 14; tileY++)
            {
                for (int tileX = 0; tileX < 16; tileX++)
                {
                    int tileMemoryOffset = sramImageBaseOffset + ((tileY * 16 + tileX) * 16);

                    for (int y = 0; y < 8; y++)
                    {
                        byte lowByte = 0;
                        byte highByte = 0;

                        for (int x = 0; x < 8; x++)
                        {
                            int pixelX = (tileX * 8) + x;
                            int pixelY = (tileY * 8) + y;

                            Color c = camFrame.GetPixel(pixelX, pixelY);

                            // Native luminance
                            int luminance = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);

                            // Apply the ROM's requested Exposure multiplier
                            luminance = Math.Clamp((int)(luminance * exposure), 0, 255);

                            // 2. PANDOCS DITHERING MATRIX (0xA006 - 0xA035)
                            // The ROM writes three separate 4x4 arrays to define the exact thresholds
                            // for the 4 hardware shades.
                            int matrixIndex = (pixelY % 4) * 4 + (pixelX % 4);

                            byte t1 = cameraRegisters[0x06 + matrixIndex]; // Threshold for Dark Gray
                            byte t2 = cameraRegisters[0x16 + matrixIndex]; // Threshold for Light Gray
                            byte t3 = cameraRegisters[0x26 + matrixIndex]; // Threshold for White

                            // 3. HARDWARE QUANTIZATION
                            // The sensor checks the exposed luminance against the ROM's thresholds
                            int shade = 3; // Default to Black
                            if (luminance >= t1) shade = 2; // Pass T1 = Dark Gray
                            if (luminance >= t2) shade = 1; // Pass T2 = Light Gray
                            if (luminance >= t3) shade = 0; // Pass T3 = White

                            // 4. PLANAR SERIALIZATION
                            int bitIndex = 7 - x;
                            if ((shade & 1) != 0) lowByte |= (byte)(1 << bitIndex);
                            if ((shade & 2) != 0) highByte |= (byte)(1 << bitIndex);
                        }

                        CartridgeRam[tileMemoryOffset + (y * 2)] = lowByte;
                        CartridgeRam[tileMemoryOffset + (y * 2) + 1] = highByte;
                    }
                }
            }
        }

        // --- ADD THESE VARIABLES ---
        private Bitmap _latestFrame;
        private readonly object _frameLock = new object();

        // The WinForms UI will call this every time the webcam captures a new frame
        public void UpdateWebcamFrame(Bitmap newFrame)
        {
            lock (_frameLock)
            {
                _latestFrame?.Dispose();
                _latestFrame = new Bitmap(newFrame, new Size(128, 112)); // Pre-scale for performance
            }
        }
        private Bitmap GetLiveWebcamFrame()
        {
            lock (_frameLock)
            {
                if (_latestFrame != null)
                {
                    // Return a clone resized exactly to the Game Boy sensor limits
                    return new Bitmap(_latestFrame, new Size(128, 112));
                }
            }

            // Fallback if the camera is still booting
            Bitmap placeholder = new Bitmap(128, 112);
            using (Graphics g = Graphics.FromImage(placeholder))
            {
                g.Clear(Color.Gray);
            }
            return placeholder;
        }
    }
}
