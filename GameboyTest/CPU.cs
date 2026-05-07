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
        public ulong TotalClockCycles { get; private set; }

        public CPU(MemoryBus memoryBus)
        {
            this.bus = memoryBus;
            ResetToPostBootromState();
        }
        // Add this to your CPU state variables

        // --- THE HEARTBEAT ---
        private void Tick()
        {
            // Every memory access takes 1 M-Cycle, which is 4 T-Cycles
            TotalClockCycles += 4;

            // NOTE FOR LATER: This is exactly where you will sync the rest of the hardware!
            // ppu.Step(4);
            // timer.Step(4);
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
        // Change from 'public int Step()' to 'public void Step()'
        public void Step()
        {
            if (Halted)
            {
                // The CPU is asleep, but the system clock still ticks!
                Tick();
                return;
            }

            // 1. Fetching the opcode takes 1 memory access (4 T-Cycles)
            byte opcode = ReadNextByte();

            // 2. Decode and Execute
            ExecuteOpcode(opcode);
        }

        // Helper to read a byte and push the PC forward automatically
        // Replace your old ReadNextByte with this:
        private byte ReadNextByte()
        {
            Tick(); // Time passes while reading...
            byte value = bus.ReadByte(PC);
            PC++;
            return value;
        }

        // New helper for reading memory from addresses other than PC (like reading RAM)
        private byte ReadMemory(ushort address)
        {
            Tick();
            return bus.ReadByte(address);
        }

        // New helper for writing to memory
        private void WriteMemory(ushort address, byte value)
        {
            Tick();
            bus.WriteByte(address, value);
        }

        // ReadNextWord automatically takes 8 T-Cycles because it calls ReadNextByte twice!
        private ushort ReadNextWord()
        {
            byte low = ReadNextByte();   // Ticks 4
            byte high = ReadNextByte();  // Ticks 4
            return (ushort)((high << 8) | low);
        }
        private void ExecuteOpcode(byte opcode)
        {
            switch (opcode)
            {
                case 0x00: // NOP (No Operation)
                    break ; 
                case 0x01: // LD BC, n16
                    BC = ReadNextWord();
                    break;
                case 0x11:
                    DE = ReadNextWord();
                    break;
                case 0x21:
                    HL = ReadNextWord();
                    break;
                case 0x31: // LD SP, d16 (Load 16-bit immediate value into SP)
                    SP = ReadNextWord();
                    break;
                case 0x02:
                    WriteMemory(BC,A); // LD (BC), A
                    break;
                case 0x12:
                    WriteMemory(DE, A);
                    break;
                case 0x22:
                    WriteMemory(HL, A);
                    HL++;
                    break;
                case 0x32:
                    WriteMemory(HL, A);
                    HL--;
                    break;
                case 0x03: // INC BC (Increment 16-bit register BC)
                    BC++; // The 8-bit ALU needs extra time to calculate the 16-bit addition
                    Tick();
                    break;
                case 0x13:
                    DE++;
                    Tick();
                    break;
                case 0x23:
                    HL++;
                    Tick();
                    break;
                case 0x33: // INC SP (Increment 16-bit Stack Pointer)
                    SP++; // The 8-bit ALU needs extra time to calculate the 16-bit addition
                    Tick();
                    break;
                case 0x04:
                    FlagH = (B & 0x0F) == 0x0F;
                    B++;
                    FlagZ = (B == 0);
                    FlagN = false;
                    break;
                case 0x14:
                    FlagH = (D & 0x0F) == 0x0F;
                    D++;
                    FlagZ = (D == 0);
                    FlagN = false;
                    break;

                case 0x24:
                    FlagH = (H & 0x0F) == 0x0F;
                    H++;
                    FlagZ = (H == 0);
                    FlagN = false;
                    break;
                case 0x34:
                    // INC (HL) (Increment the value at the memory address pointed to by HL)
                    byte valueAtHL = ReadMemory(HL);
                    FlagH = (valueAtHL & 0x0F) == 0x0F;
                    valueAtHL++;
                    WriteMemory(HL, valueAtHL);
                    FlagZ = (valueAtHL == 0);
                    FlagN = false;
                    break;

                case 0x05: // DEC B (Decrement 8-bit register B)
                    B--;
                    // Update flags
                    FlagZ = (B == 0);
                    FlagN = true;
                    FlagH = (B & 0x0F) == 0x0F;
                    break;
                case 0x15:
                    D--;
                    // Update flags
                    FlagZ = (D == 0);
                    FlagN = true;
                    FlagH = (D & 0x0F) == 0x0F;
                    break;
                case 0x25:
                    H--;
                    // Update flags
                    FlagZ = (H == 0);
                    FlagN = true;
                    FlagH = (H & 0x0F) == 0x0F;
                    break;
                case 0x35: // DEC (HL) (Decrement value in memory at address HL)
                    byte decMemValue = ReadMemory(HL);
                    decMemValue--;
                    FlagZ = (decMemValue == 0);
                    FlagN = true;
                    FlagH = (decMemValue & 0x0F) == 0x0F;
                    WriteMemory(HL, decMemValue);
                    break;

                case 0x06:
                    B = ReadNextByte();
                    break;
                case 0x3E: // LD A, d8 (Load 8-bit immediate value into A)
                    A = ReadNextByte();
                    break;

                case 0xAF: // XOR A (Exclusive OR register A with itself)
                    // This is the most common way games set A to 0!
                    A ^= A;
                    FlagZ = (A == 0);
                    FlagN = false;
                    FlagH = false;
                    FlagC = false;
                    break;

                case 0xC3: // JP a16 (Jump to 16-bit address)
                    ushort jumpAddress = ReadNextWord();
                    PC = jumpAddress;
                    Tick();
                    break;

                case 0xCB: // PREFIX CB (Extended Instructions)
                    ExecuteCbOpcode(ReadNextByte());
                    break;

                default:
                    throw new NotImplementedException($"Opcode 0x{opcode:X2} at PC 0x{PC - 1:X4} is not implemented!");
            }
        }

        private void ExecuteCbOpcode(byte cbOpcode)
        {
            // The CB prefix gives access to 256 MORE instructions (Bit shifting, setting, testing)
            switch (cbOpcode)
            {
                case 0x7C: // BIT 7, H (Test if bit 7 of register H is 1)
                    FlagZ = ((H & 0x80) == 0); // Z is set if the bit is 0!
                    FlagN = false;
                    FlagH = true;
                    // Carry flag is untouched
                    Tick();
                    break;

                default:
                    throw new NotImplementedException($"CB Opcode 0x{cbOpcode:X2} at PC 0x{PC - 2:X4} is not implemented!");
            }
        }
    }
}