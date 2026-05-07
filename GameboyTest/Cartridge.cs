using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace GameboyTest
{
    public class Cartridge
    {
        public string Title { get; private set; }
        public bool IsLoaded { get; private set; }

        // Mapped Header Info
        public string MbcType { get; private set; }
        public string HardwareFeature { get; private set; }
        public int TotalRomBanks { get; private set; }
        public int TotalRamBanks { get; private set; }

        // The split memory banks!
        public byte[][] RomBanks { get; private set; }
        public byte[][] RamBanks { get; private set; }

        // --- C# Equivalents of your Python Dictionaries ---

        private readonly Dictionary<byte, (string Mbc, string Feature)> MbcTypeMap = new Dictionary<byte, (string, string)>
        {
            { 0x00, ("None", "None") },
            { 0x01, ("MBC1", "None") },
            { 0x02, ("MBC1", "RAM") },
            { 0x03, ("MBC1", "Battery-Buffered RAM") },
            { 0x05, ("MBC2", "None") },
            { 0x06, ("MBC2", "RAM") },
            { 0x08, ("None", "RAM") },
            { 0x09, ("None", "Battery-Buffered RAM") }, // Corrected from 0x08 duplicate in original
            { 0x0B, ("MM01", "None") },
            { 0x0C, ("MM01", "RAM") },
            { 0x0D, ("MM01", "Battery-Buffered RAM") },
            { 0x0F, ("MBC3", "Real Time Clock") },
            { 0x10, ("MBC3", "RTC + Battery-Buffered RAM") },
            { 0x11, ("MBC3", "None") },
            { 0x12, ("MBC3", "RAM") },
            { 0x13, ("MBC3", "Battery-Buffered RAM") },
            { 0x19, ("MBC5", "None") },
            { 0x1A, ("MBC5", "RAM") },
            { 0x1B, ("MBC5", "Battery-Buffered RAM") }, // Fixed typo in MBC51
            { 0x1C, ("MBC5", "Rumble") },
            { 0x1D, ("MBC5", "Rumble+RAM") },
            { 0x1E, ("MBC5", "Battery-Buffered RAM") },
            { 0x20, ("MBC6", "None") }
        };

        // Standard Game Boy ROM banks are always 16KB (16,384 bytes)
        private readonly Dictionary<byte, int> RomBankCountMap = new Dictionary<byte, int>
        {
            { 0x00, 2 },   // 32KB = 2 banks (Base Game)
            { 0x01, 4 },   // 64KB = 4 banks
            { 0x02, 8 },   // 128KB = 8 banks
            { 0x03, 16 },  // 256KB = 16 banks
            { 0x04, 32 },  // 512KB = 32 banks
            { 0x05, 64 },  // 1MB = 64 banks
            { 0x06, 128 }, // 2MB = 128 banks
            { 0x07, 256 }  // 4MB = 256 banks
        };

        // Values mapped directly to your total byte sizes
        private readonly Dictionary<byte, int> RamTotalSizeMap = new Dictionary<byte, int>
        {
            { 0x00, 0 },
            { 0x01, 2048 },   // 2KB (1 partial bank)
            { 0x02, 8192 },   // 8KB (1 full bank)
            { 0x03, 32768 },  // 32KB (4 banks of 8KB)
            { 0x04, 131072 }, // 128KB (16 banks of 8KB)
            { 0x05, 65536 }   // 64KB (8 banks of 8KB)
        };

        public bool LoadRom(string filePath)
        {
            try
            {
                byte[] rawFile = File.ReadAllBytes(filePath);

                if (rawFile.Length < 32768) return false;

                ParseHeader(rawFile);
                SplitRomBanks(rawFile);
                InitializeRamBanks();

                IsLoaded = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ParseHeader(byte[] rawFile)
        {
            // 1. Get Title (0x0134 - 0x0143)
            StringBuilder titleBuilder = new StringBuilder();
            for (int i = 0x0134; i <= 0x0143; i++)
            {
                if (rawFile[i] == 0) break;
                titleBuilder.Append((char)rawFile[i]);
            }
            Title = titleBuilder.ToString().Trim();

            // 2. Get Cartridge Type (0x0147)
            byte typeCode = rawFile[0x0147];
            if (MbcTypeMap.ContainsKey(typeCode))
            {
                var hardware = MbcTypeMap[typeCode];
                MbcType = hardware.Mbc;
                HardwareFeature = hardware.Feature;
            }

            // 3. Get ROM Size (0x0148)
            byte romCode = rawFile[0x0148];
            TotalRomBanks = RomBankCountMap.ContainsKey(romCode) ? RomBankCountMap[romCode] : 2;

            // 4. Get RAM Size (0x0149)
            byte ramCode = rawFile[0x0149];
            int totalRamBytes = RamTotalSizeMap.ContainsKey(ramCode) ? RamTotalSizeMap[ramCode] : 0;

            // Standard GB RAM banks are 8KB (8192 bytes). We calculate how many banks we need.
            TotalRamBanks = totalRamBytes > 0 ? Math.Max(1, totalRamBytes / 8192) : 0;
        }

        private void SplitRomBanks(byte[] rawFile)
        {
            int bankSize = 16384; // 16KB per bank
            RomBanks = new byte[TotalRomBanks][];

            for (int i = 0; i < TotalRomBanks; i++)
            {
                RomBanks[i] = new byte[bankSize];

                // Calculate where to start copying from the giant raw file
                int sourceOffset = i * bankSize;

                // Ensure we don't read past the end of the file if it's a bad dump
                int copyLength = Math.Min(bankSize, rawFile.Length - sourceOffset);

                if (copyLength > 0)
                {
                    // Fast memory copy from the 1D raw file into our 2D array
                    Array.Copy(rawFile, sourceOffset, RomBanks[i], 0, copyLength);
                }
            }
        }

        private void InitializeRamBanks()
        {
            int bankSize = 8192; // 8KB per bank
            RamBanks = new byte[TotalRamBanks][];

            for (int i = 0; i < TotalRamBanks; i++)
            {
                // Create clean, empty arrays for the MBC to use
                RamBanks[i] = new byte[bankSize];
            }
        }
    }
}