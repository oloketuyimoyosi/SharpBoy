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
        public void Step()
        {
            CheckInterrupts();
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
            PC++;
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
        private void CheckInterrupts()
        {
            // Use bus.ReadByte() instead of ReadMemory() because checking these 
            // lines happens instantly internally; it doesn't take extra T-Cycles.
            byte ie = bus.ReadByte(0xFFFF);
            byte iff = bus.ReadByte(0xFF0F);

            // A bitwise AND tells us if any allowed interrupts are currently requesting to fire
            byte pendingInterrupts = (byte)(ie & iff);

            if (pendingInterrupts > 0)
            {
                // WAKE UP! If an interrupt is pending, the CPU instantly leaves the Halt state,
                // even if IME (Interrupt Master Enable) is currently false!
                Halted = false;

                // However, we only actually JUMP to the interrupt code if the Master switch is ON.
                if (IME)
                {
                    // 1. Disable the master switch so we don't get interrupted while handling an interrupt
                    IME = false;

                    // 2. The CPU requires 8 T-Cycles of internal delay to prepare for the jump
                    Tick();
                    Tick();

                    // 3. Push the current Program Counter (PC) to the Stack so we can return later
                    SP--;
                    WriteMemory(SP, (byte)(PC >> 8));   // Push upper byte (Ticks 4)
                    SP--;
                    WriteMemory(SP, (byte)(PC & 0xFF)); // Push lower byte (Ticks 4)

                    // 4. Figure out exactly WHICH interrupt fired and jump to it!
                    if ((pendingInterrupts & 0x01) != 0)      // Bit 0: VBlank
                        ExecuteInterrupt(0, 0x0040);
                    else if ((pendingInterrupts & 0x02) != 0) // Bit 1: LCD STAT
                        ExecuteInterrupt(1, 0x0048);
                    else if ((pendingInterrupts & 0x04) != 0) // Bit 2: Timer
                        ExecuteInterrupt(2, 0x0050);
                    else if ((pendingInterrupts & 0x08) != 0) // Bit 3: Serial
                        ExecuteInterrupt(3, 0x0058);
                    else if ((pendingInterrupts & 0x10) != 0) // Bit 4: Joypad
                        ExecuteInterrupt(4, 0x0060);
                }
            }
        }

        private void ExecuteInterrupt(int bit, ushort jumpAddress)
        {
            // 1. Clear the specific bit in the IF register so it doesn't keep firing endlessly
            byte iff = bus.ReadByte(0xFF0F);
            iff &= (byte)~(1 << bit);
            bus.WriteByte(0xFF0F, iff);

            // 2. Jump to the hardcoded memory address for this specific interrupt
            PC = jumpAddress;

            // 3. The final jump takes 4 T-Cycles
            Tick();
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
                case 0x16:
                    D = ReadNextByte();
                    break;
                case 0x26:
                    H = ReadNextByte();
                    break;
                case 0x36:
                    byte immediateValue = ReadNextByte();
                    WriteMemory(HL, immediateValue);
                    break;
                // --- ROTATE & JUMPS ---
                case 0x17: // RLA (Rotate A Left through Carry)
                    bool oldCarryRLA = FlagC;
                    FlagC = (A & 0x80) != 0; // The 7th bit goes into the carry
                    A = (byte)((A << 1) | (oldCarryRLA ? 1 : 0)); // Old carry goes into 0th bit
                    FlagZ = false; FlagN = false; FlagH = false; // RLA always clears Z, N, and H
                    break;

                case 0x18: // JR r8 (Jump Relative - Unconditional)
                    // The offset is a SIGNED 8-bit integer (-128 to 127). 
                    // This allows jumping backward in memory (loops)!
                    sbyte offset18 = (sbyte)ReadNextByte();
                    PC = (ushort)(PC + offset18);
                    Tick(); // Internal delay to update the PC
                    break;

                // --- 16-BIT MATH ---
                case 0x19: // ADD HL, DE
                    int result19 = HL + DE;
                    FlagN = false;
                    // Half-carry for 16-bit happens at the 11th bit (0xFFF)
                    FlagH = ((HL & 0xFFF) + (DE & 0xFFF)) > 0xFFF;
                    FlagC = result19 > 0xFFFF;
                    HL = (ushort)result19;
                    Tick(); // 16-bit math takes an extra internal cycle
                    break;

                // --- LOADS & 8-BIT MATH ---
                case 0x1A: // LD A, (DE)
                    A = ReadMemory(DE);
                    break;

                case 0x1B: // DEC DE
                    DE--;
                    Tick(); // 16-bit math delay
                    break;

                case 0x1C: // INC E
                    FlagH = (E & 0x0F) == 0x0F;
                    E++;
                    FlagZ = (E == 0); FlagN = false;
                    break;

                case 0x1D: // DEC E
                    E--;
                    FlagZ = (E == 0); FlagN = true;
                    FlagH = (E & 0x0F) == 0x0F;
                    break;

                case 0x1E: // LD E, d8
                    E = ReadNextByte();
                    break;

                case 0x1F: // RRA (Rotate A Right through Carry)
                    bool oldCarryRRA = FlagC;
                    FlagC = (A & 0x01) != 0; // The 0th bit goes into the carry
                    A = (byte)((A >> 1) | (oldCarryRRA ? 0x80 : 0)); // Old carry goes into 7th bit
                    FlagZ = false; FlagN = false; FlagH = false;
                    break;

                // --- CONDITIONAL JUMPS ---
                case 0x20: // JR NZ, r8 (Jump Relative if Not Zero)
                    sbyte offset20 = (sbyte)ReadNextByte();
                    if (!FlagZ)
                    {
                        PC = (ushort)(PC + offset20);
                        Tick(); // Only ticks if the jump is actually taken!
                    }
                    break;
                // --- THE INFAMOUS DAA ---
                case 0x27: // DAA (Decimal Adjust Accumulator)
                    // This fixes math when dealing with Binary Coded Decimal (BCD) like Tetris scores.
                    int daaValue = A;
                    if (!FlagN) // After an addition
                    {
                        if (FlagC || daaValue > 0x99) { daaValue += 0x60; FlagC = true; }
                        if (FlagH || (daaValue & 0x0F) > 0x09) { daaValue += 0x06; }
                    }
                    else // After a subtraction
                    {
                        if (FlagC) { daaValue -= 0x60; }
                        if (FlagH) { daaValue -= 0x06; }
                    }
                    A = (byte)daaValue;
                    FlagZ = (A == 0);
                    FlagH = false; // DAA always clears the Half-Carry flag
                    break;

                case 0x28: // JR Z, r8 (Jump Relative if Zero)
                    sbyte offset28 = (sbyte)ReadNextByte();
                    if (FlagZ)
                    {
                        PC = (ushort)(PC + offset28);
                        Tick();
                    }
                    break;

                case 0x29: // ADD HL, HL
                    int result29 = HL + HL;
                    FlagN = false;
                    FlagH = ((HL & 0xFFF) + (HL & 0xFFF)) > 0xFFF;
                    FlagC = result29 > 0xFFFF;
                    HL = (ushort)result29;
                    Tick();
                    break;

                case 0x2A: // LD A, (HL+) (Often written as LDI A, (HL))
                    // Load memory at HL into A, then instantly increment HL
                    A = ReadMemory(HL);
                    HL++;
                    break;

                case 0x2B: // DEC HL
                    HL--;
                    Tick();
                    break;

                case 0x2C: // INC L
                    FlagH = (L & 0x0F) == 0x0F;
                    L++;
                    FlagZ = (L == 0); FlagN = false;
                    break;

                case 0x2D: // DEC L
                    L--;
                    FlagZ = (L == 0); FlagN = true;
                    FlagH = (L & 0x0F) == 0x0F;
                    break;

                case 0x2E: // LD L, d8
                    L = ReadNextByte();
                    break;

                case 0x2F: // CPL (Complement A - Flips all bits)
                    A = (byte)~A;
                    FlagN = true;
                    FlagH = true;
                    // CPL does not touch Z or C flags
                    break;

                case 0x30: // JR NC, r8 (Jump Relative if Not Carry)
                    sbyte offset30 = (sbyte)ReadNextByte();
                    if (!FlagC)
                    {
                        PC = (ushort)(PC + offset30);
                        Tick();
                    }
                    break;
                case 0x37: // SCF (Set Carry Flag)
                    FlagN = false;
                    FlagH = false;
                    FlagC = true;
                    // SCF does not touch the Z flag
                    break;

                case 0x07: // RLCA (Rotate Accumulator Left)
                           // This shifts all bits in A to the left. 
                           // The highest bit (Bit 7) gets put into the Carry Flag AND wraps around to Bit 0.
                    bool bit7 = (A & 0x80) != 0; // Check if the highest bit is 1
                    A = (byte)((A << 1) | (bit7 ? 1 : 0));

                    // Hardware quirk: RLCA always clears Z, N, and H. It only updates C.
                    FlagZ = false;
                    FlagN = false;
                    FlagH = false;
                    FlagC = bit7;
                    break;
                case 0x3E: // LD A, d8 (Load 8-bit immediate value into A)
                    A = ReadNextByte();
                    break;
                // --- 8-BIT LOADS (Destination: Register B) ---
                case 0x40: // LD B, B 
                    // B = B; (This does absolutely nothing, it acts like a 4-cycle NOP!)
                    break;
                case 0x41: // LD B, C
                    B = C;
                    break;
                case 0x42: // LD B, D
                    B = D;
                    break;
                case 0x43: // LD B, E
                    B = E;
                    break;
                case 0x44: // LD B, H
                    B = H;
                    break;
                case 0x45: // LD B, L
                    B = L;
                    break;
                case 0x46: // LD B, (HL)
                    // Memory Accurate: 4 cycles for opcode + 4 cycles for ReadMemory = 8 cycles
                    B = ReadMemory(HL);
                    break;
                case 0x47: // LD B, A
                    B = A;
                    break;

                // --- 8-BIT LOADS (Destination: Register C) ---
                case 0x48: // LD C, B
                    C = B;
                    break;
                case 0x49: // LD C, C
                    // C = C; 
                    break;
                case 0x4A: // LD C, D
                    C = D;
                    break;
                case 0x4B: // LD C, E
                    C = E;
                    break;
                case 0x4C: // LD C, H
                    C = H;
                    break;
                case 0x4D: // LD C, L
                    C = L;
                    break;
                case 0x4E: // LD C, (HL)
                    C = ReadMemory(HL);
                    break;
                case 0x4F: // LD C, A
                    C = A;
                    break;

                // --- 8-BIT LOADS (Destination: Register D) ---
                case 0x50: // LD D, B
                    D = B;
                    break;
                case 0x51: // LD D, C
                    D = C;
                    break;
                case 0x52: // LD D, D
                    // D = D; (Acts like a 4-cycle NOP)
                    break;
                case 0x53: // LD D, E
                    D = E;
                    break;
                case 0x54: // LD D, H
                    D = H;
                    break;
                case 0x55: // LD D, L
                    D = L;
                    break;
                case 0x56: // LD D, (HL)
                    // Memory Accurate: 4 cycles for opcode + 4 cycles for ReadMemory = 8 cycles
                    D = ReadMemory(HL);
                    break;
                case 0x57: // LD D, A
                    D = A;
                    break;

                // --- 8-BIT LOADS (Destination: Register E) ---
                case 0x58: // LD E, B
                    E = B;
                    break;
                case 0x59: // LD E, C
                    E = C;
                    break;
                case 0x5A: // LD E, D
                    E = D;
                    break;
                case 0x5B: // LD E, E
                    // E = E; (Acts like a 4-cycle NOP)
                    break;
                case 0x5C: // LD E, H
                    E = H;
                    break;
                case 0x5D: // LD E, L
                    E = L;
                    break;
                case 0x5E: // LD E, (HL)
                    E = ReadMemory(HL);
                    break;
                case 0x5F: // LD E, A
                    E = A;
                    break;
                // --- 8-BIT LOADS (Destination: Register H) ---
                case 0x60: // LD H, B
                    H = B;
                    break;
                case 0x61: // LD H, C
                    H = C;
                    break;
                case 0x62: // LD H, D
                    H = D;
                    break;
                case 0x63: // LD H, E
                    H = E;
                    break;
                case 0x64: // LD H, H
                    // H = H; (Acts like a 4-cycle NOP)
                    break;
                case 0x65: // LD H, L
                    H = L;
                    break;
                case 0x66: // LD H, (HL)
                    // Memory Accurate: 4 cycles for opcode + 4 cycles for ReadMemory = 8 cycles
                    H = ReadMemory(HL);
                    break;
                case 0x67: // LD H, A
                    H = A;
                    break;

                // --- 8-BIT LOADS (Destination: Register L) ---
                case 0x68: // LD L, B
                    L = B;
                    break;
                case 0x69: // LD L, C
                    L = C;
                    break;
                case 0x6A: // LD L, D
                    L = D;
                    break;
                case 0x6B: // LD L, E
                    L = E;
                    break;
                case 0x6C: // LD L, H
                    L = H;
                    break;
                case 0x6D: // LD L, L
                    // L = L; (Acts like a 4-cycle NOP)
                    break;
                case 0x6E: // LD L, (HL)
                    L = ReadMemory(HL);
                    break;
                case 0x6F: // LD L, A
                    L = A;
                    break;
                // --- 8-BIT LOADS (Destination: Memory at HL) ---
                case 0x70: // LD (HL), B
                    // Memory Accurate: 4 cycles for opcode + 4 cycles for WriteMemory = 8 cycles
                    WriteMemory(HL, B);
                    break;
                case 0x71: // LD (HL), C
                    WriteMemory(HL, C);
                    break;
                case 0x72: // LD (HL), D
                    WriteMemory(HL, D);
                    break;
                case 0x73: // LD (HL), E
                    WriteMemory(HL, E);
                    break;
                case 0x74: // LD (HL), H
                    WriteMemory(HL, H);
                    break;
                case 0x75: // LD (HL), L
                    WriteMemory(HL, L);
                    break;

                // --- THE SLEEP COMMAND ---
                case 0x76: // HALT
                    // The CPU stops executing instructions here, but the internal clock keeps ticking
                    // until a hardware interrupt (like a VBlank or Button Press) fires.
                    Halted = true;
                    break;

                // --- 8-BIT LOADS (Destination: Memory at HL) ---
                case 0x77: // LD (HL), A
                    WriteMemory(HL, A);
                    break;

                // --- 8-BIT LOADS (Destination: Register A / Accumulator) ---
                case 0x78: // LD A, B
                    A = B;
                    break;
                case 0x79: // LD A, C
                    A = C;
                    break;
                case 0x7A: // LD A, D
                    A = D;
                    break;
                case 0x7B: // LD A, E
                    A = E;
                    break;
                case 0x7C: // LD A, H
                    A = H;
                    break;
                case 0x7D: // LD A, L
                    A = L;
                    break;
                case 0x7E: // LD A, (HL)
                    A = ReadMemory(HL);
                    break;
                case 0x7F: // LD A, A
                    // A = A; (Acts like a 4-cycle NOP)
                    break;
                // --- ADDITION (ADD A, register) ---
                case 0x80: // ADD A, B
                    AddA(B);
                    break;
                case 0x81: // ADD A, C
                    AddA(C);
                    break;
                case 0x82: // ADD A, D
                    AddA(D);
                    break;
                case 0x83: // ADD A, E
                    AddA(E);
                    break;
                case 0x84: // ADD A, H
                    AddA(H);
                    break;
                case 0x85: // ADD A, L
                    AddA(L);
                    break;
                case 0x86: // ADD A, (HL)
                    // Memory Accurate: 4 cycles (opcode) + 4 cycles (memory read) = 8 cycles
                    AddA(ReadMemory(HL));
                    break;
                case 0x87: // ADD A, A
                    AddA(A);
                    break;

                // --- ADDITION WITH CARRY (ADC A, register) ---
                case 0x88: // ADC A, B
                    AdcA(B);
                    break;
                case 0x89: // ADC A, C
                    AdcA(C);
                    break;
                case 0x8A: // ADC A, D
                    AdcA(D);
                    break;
                case 0x8B: // ADC A, E
                    AdcA(E);
                    break;
                case 0x8C: // ADC A, H
                    AdcA(H);
                    break;
                case 0x8D: // ADC A, L
                    AdcA(L);
                    break;
                case 0x8E: // ADC A, (HL)
                    AdcA(ReadMemory(HL));
                    break;
                case 0x8F: // ADC A, A
                    AdcA(A);
                    break;
                // --- SUBTRACTION (SUB A, register) ---
                case 0x90: // SUB A, B
                    SubA(B);
                    break;
                case 0x91: // SUB A, C
                    SubA(C);
                    break;
                case 0x92: // SUB A, D
                    SubA(D);
                    break;
                case 0x93: // SUB A, E
                    SubA(E);
                    break;
                case 0x94: // SUB A, H
                    SubA(H);
                    break;
                case 0x95: // SUB A, L
                    SubA(L);
                    break;
                case 0x96: // SUB A, (HL)
                    // Memory Accurate: 4 cycles (opcode) + 4 cycles (memory read) = 8 cycles
                    SubA(ReadMemory(HL));
                    break;
                case 0x97: // SUB A, A
                    SubA(A); // This is effectively A = 0, but properly sets all the flags!
                    break;

                // --- SUBTRACTION WITH CARRY (SBC A, register) ---
                case 0x98: // SBC A, B
                    SbcA(B);
                    break;
                case 0x99: // SBC A, C
                    SbcA(C);
                    break;
                case 0x9A: // SBC A, D
                    SbcA(D);
                    break;
                case 0x9B: // SBC A, E
                    SbcA(E);
                    break;
                case 0x9C: // SBC A, H
                    SbcA(H);
                    break;
                case 0x9D: // SBC A, L
                    SbcA(L);
                    break;
                case 0x9E: // SBC A, (HL)
                    SbcA(ReadMemory(HL));
                    break;
                case 0x9F: // SBC A, A
                    SbcA(A);
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



        // --- ALU HELPER METHODS ---

        private void AddA(byte value)
        {
            int result = A + value;

            // Update Flags
            FlagZ = ((result & 0xFF) == 0);
            FlagN = false; // N is always reset for addition

            // Half-carry happens if bits 0-3 overflow into bit 4
            FlagH = ((A & 0x0F) + (value & 0x0F) > 0x0F);

            // Full carry happens if the result exceeds 255 (8 bits)
            FlagC = (result > 0xFF);

            // Finally, store the lowest 8 bits back into the Accumulator
            A = (byte)result;
        }

        private void AdcA(byte value)
        {
            int carry = FlagC ? 1 : 0; // Fetch the current carry flag
            int result = A + value + carry;

            // Update Flags
            FlagZ = ((result & 0xFF) == 0);
            FlagN = false;

            // Half-carry must factor in the previous carry!
            FlagH = ((A & 0x0F) + (value & 0x0F) + carry > 0x0F);
            FlagC = (result > 0xFF);

            A = (byte)result;
        }
        private void SubA(byte value)
        {
            int result = A - value;

            // Update Flags
            FlagZ = ((result & 0xFF) == 0);

            // N is ALWAYS set (1) for subtraction
            FlagN = true;

            // Half-carry happens if we need to borrow from bit 4 to satisfy the lower nibble
            FlagH = ((A & 0x0F) - (value & 0x0F) < 0);

            // Full carry happens if the value being subtracted is larger than A
            FlagC = (A < value);

            // Finally, store the lowest 8 bits back into the Accumulator
            A = (byte)result;
        }

        private void SbcA(byte value)
        {
            int carry = FlagC ? 1 : 0; // Fetch the current borrow flag
            int result = A - value - carry;

            // Update Flags
            FlagZ = ((result & 0xFF) == 0);
            FlagN = true;

            // Half-carry borrow must factor in the previous carry!
            FlagH = ((A & 0x0F) - (value & 0x0F) - carry < 0);

            // Full carry borrow
            FlagC = (result < 0);

            A = (byte)result;
        }
    }
}