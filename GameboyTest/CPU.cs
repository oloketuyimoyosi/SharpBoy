using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;

namespace GameboyTest
{
    public class CPU
    {
        private MemoryBus bus;

        // --- 8-BIT REGISTERS ---
        public byte A, B, C, D, E, H, L;

        // --- FLAGS REGISTER (F) ---
        // Bit 7: Zero (Z)
        // Bit 6: Subtract (N)
        // Bit 5: Half-Carry (H)
        // Bit 4: Carry (C)
        // Bits 3-0 are always 0!
        public byte F;

        // --- 16-BIT REGISTERS ---
        public ushort SP { get; set; } // Stack Pointer
        public ushort PC { get; set; } // Program Counter

        // --- 16-BIT PAIRED REGISTERS (Virtual) ---
        // The Game Boy allows combining two 8-bit registers into one 16-bit register.
        public ushort AF
        {
            get => (ushort)((A << 8) | F);
            set { A = (byte)(value >> 8); F = (byte)(value & 0xF0); } // Lower 4 bits of F must always be 0
        }
        public ushort BC
        {
            get => (ushort)((B << 8) | C);
            set { B = (byte)(value >> 8); C = (byte)(value & 0xFF); }
        }
        public ushort DE
        {
            get => (ushort)((D << 8) | E);
            set { D = (byte)(value >> 8); E = (byte)(value & 0xFF); }
        }
        public ushort HL
        {
            get => (ushort)((H << 8) | L);
            set { H = (byte)(value >> 8); L = (byte)(value & 0xFF); }
        }

        // --- FLAG HELPERS ---
        // These make it incredibly easy to set and check specific conditions
        public bool FlagZ
        {
            get => (F & 0x80) != 0;
            set => F = (byte)(value ? F | 0x80 : F & ~0x80);
        }
        public bool FlagN
        {
            get => (F & 0x40) != 0;
            set => F = (byte)(value ? F | 0x40 : F & ~0x40);
        }
        public bool FlagH
        {
            get => (F & 0x20) != 0;
            set => F = (byte)(value ? F | 0x20 : F & ~0x20);
        }
        public bool FlagC
        {
            get => (F & 0x10) != 0;
            set => F = (byte)(value ? F | 0x10 : F & ~0x10);
        }

        // --- CPU STATE ---
        public bool IME { get; set; } // Interrupt Master Enable
        public bool Halted { get; set; }

        public CPU(MemoryBus memoryBus)
        {
            this.bus = memoryBus;
            ResetToPostBootromState();
        }

        private void ResetToPostBootromState()
        {
            // If you skip the Nintendo logo, these are the EXACT values 
            // the hardware has the moment the game starts at 0x0100.
            AF = 0x01B0;
            BC = 0x0013;
            DE = 0x00D8;
            HL = 0x014D;
            SP = 0xFFFE;
            PC = 0x0100;

            IME = false;
            Halted = false;
        }

        // Returns the number of T-Cycles (clock cycles) the instruction took.
        // This is crucial for syncing the audio and graphics later!
        public int Step()
        {
            if (Halted)
            {
                // If halted, CPU does nothing but wait for an interrupt (takes 4 cycles)
                return 4;
            }

            // 1. Fetch the opcode at the current PC
            byte opcode = ReadNextByte();

            // 2. Decode and Execute
            return ExecuteOpcode(opcode);
        }

        // Helper to read a byte and push the PC forward automatically
        private byte ReadNextByte()
        {
            byte value = bus.ReadByte(PC);
            PC++;
            return value;
        }

        private ushort ReadNextWord()
        {
            // Game Boy is Little-Endian! (Lower byte comes first)
            byte low = ReadNextByte();
            byte high = ReadNextByte();
            return (ushort)((high << 8) | low);
        }

        private int ExecuteOpcode(byte opcode)
        {
            switch (opcode)
            {
                case 0x00: // NOP (No Operation)
                    return 4; // Takes 4 T-cycles

                case 0x31: // LD SP, d16 (Load 16-bit immediate value into SP)
                    SP = ReadNextWord();
                    return 12;

                case 0x3E: // LD A, d8 (Load 8-bit immediate value into A)
                    A = ReadNextByte();
                    return 8;

                case 0xAF: // XOR A (Exclusive OR register A with itself)
                    // This is the most common way games set A to 0!
                    A ^= A;
                    FlagZ = (A == 0);
                    FlagN = false;
                    FlagH = false;
                    FlagC = false;
                    return 4;

                case 0xC3: // JP a16 (Jump to 16-bit address)
                    ushort jumpAddress = ReadNextWord();
                    PC = jumpAddress;
                    return 16;

                case 0xCB: // PREFIX CB (Extended Instructions)
                    return ExecuteCbOpcode(ReadNextByte());

                default:
                    throw new NotImplementedException($"Opcode 0x{opcode:X2} at PC 0x{PC - 1:X4} is not implemented!");
            }
        }

        private int ExecuteCbOpcode(byte cbOpcode)
        {
            // The CB prefix gives access to 256 MORE instructions (Bit shifting, setting, testing)
            switch (cbOpcode)
            {
                case 0x7C: // BIT 7, H (Test if bit 7 of register H is 1)
                    FlagZ = ((H & 0x80) == 0); // Z is set if the bit is 0!
                    FlagN = false;
                    FlagH = true;
                    // Carry flag is untouched
                    return 8;

                default:
                    throw new NotImplementedException($"CB Opcode 0x{cbOpcode:X2} at PC 0x{PC - 2:X4} is not implemented!");
            }
        }
    }
}