using System;
using System.IO;
using System.Text;

namespace GameboyTest
{
    public class Cartridge
    {
        // This array will hold the entire game
        public byte[] RomData { get; private set; }

        // Properties we can extract from the header
        public string Title { get; private set; }
        public bool IsLoaded { get; private set; }

        public bool LoadRom(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Console.WriteLine("Error: File not found.");
                    return false;
                }

                // Read the entire binary file into our byte array
                RomData = File.ReadAllBytes(filePath);

                // A valid Game Boy ROM is always at least 32KB (32,768 bytes)
                if (RomData.Length < 32768)
                {
                    Console.WriteLine("Error: File is too small to be a valid Game Boy ROM.");
                    return false;
                }

                ParseHeader();
                IsLoaded = true;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load ROM: {ex.Message}");
                return false;
            }
        }

        private void ParseHeader()
        {
            // The game title is located from byte 0x0134 to 0x0143 (16 bytes maximum)
            // It is stored as standard uppercase ASCII characters.

            StringBuilder titleBuilder = new StringBuilder();

            for (int i = 0x0134; i <= 0x0143; i++)
            {
                byte c = RomData[i];

                // Stop reading if we hit a null terminator (0x00)
                if (c == 0) break;

                titleBuilder.Append((char)c);
            }

            Title = titleBuilder.ToString().Trim();
            
        }
    }
}