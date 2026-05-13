using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameboyTest
{
    public class CpuTestState
    {
        public string name { get; set; }
        public CpuState initial { get; set; }
        public CpuState final { get; set; }
        public object[][] cycles { get; set; } // We can ignore cycles for now unless you want strict timing
    }

    public class CpuState
    {
        public ushort pc { get; set; }
        public ushort sp { get; set; }
        public byte a { get; set; }
        public byte b { get; set; }
        public byte c { get; set; }
        public byte d { get; set; }
        public byte e { get; set; }
        public byte f { get; set; }
        public byte h { get; set; }
        public byte l { get; set; }
        // JSON provides RAM as an array of arrays: [ [address, value], [address, value] ]
        public int[][] ram { get; set; }
    }
}
