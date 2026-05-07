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
        public int calculated_bank = 0;
        public int checker = 0;



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

        }
    }
}