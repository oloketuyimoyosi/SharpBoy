using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameboyTest.MBC
{
    public interface IMbc
    {
        byte Read(ushort address);
        void Write(ushort address, byte value);
    }
}