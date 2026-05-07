using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace GameboyTest.MBC
{
    public class Mbc0 : IMbc
    {
        private byte[][] romBanks;
        private bool _RamEnabled = false;
        const ushort ROM_LOW_END = 0x3FFF;// MBC0 does not support RAM, but we include this for consistency
        const ushort ROM_HIGH_START = 0x4000;
        const ushort ROM_HIGH_END = 0x7FFF;
        private byte _offset = default;
        public byte calculated_bank = 0; 




        public Mbc0(byte[][] romBanks)
        {
            this.romBanks = romBanks;
        }

        public byte Read(ushort address)
        {
            // Figure out which 16KB bank the address belongs to
            int bank = address / 16384;

            // Figure out exactly where inside that specific bank the byte lives
            int offset = address % 16384;

            // Make sure we don't read past the end of the arrays
            if (bank < romBanks.Length)
            {
                return romBanks[bank][offset];
            }

            return 0xFF;
        }

        public void Write(ushort address, byte value)
        {
            if (address <= 0x7FFF)
            {
                if (address <= 0x1FFF)
                {
                    _RamEnabled = (value & 0x0F) == 0x0A;
                }
                // MBC0 does not support RAM, so we ignore writes to the ROM area
               
            }
        }
    }
}