using GameboyTest.Debugger;

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
        public bool Interrupt_on_Line = false;
        public ushort PC { get; set; } // Program Counter
        private bool haltBugTriggered = false;

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
        public CpuLogger Logger { get; private set; } = new CpuLogger();
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
            bus.SystemTimer.Tick(4);
            bus.ppu.Tick(4,PC, Halted,bus.ieRegister,IME,Interrupt_on_Line);
            // NOTE FOR LATER: This is exactly where you will sync the rest of the hardware!
            //ppu.Step(4);
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
        /*public void Step()
        {
            Logger.LogPC(PC);
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
        }*/
        public void Step()
        {
            // 1. HANDLE HALT STATE
            if (Halted)
            {
                // Check if an interrupt is pending (IE & IF)
                // If any enabled interrupt is pending, wake up!
                byte ie = bus.ReadByte(0xFFFF);
                byte iff = bus.ReadByte(0xFF0F);
                if (((bus.ieRegister & iff) & 0x1F) != 0)
                {
                    Halted = false;
                    PC--;
                }
                else
                {
                    // Still halted: Consume 4 T-cycles and exit Step
                    Tick();
                    return;
                }
            }

            // 2. CHECK INTERRUPTS
            // This handles the jump to the interrupt vector if IME is set

            
            // 3. LOGGING (Optional but recommended for your 0x0120 debug)
            // bus.Logger.LogPC(PC);

            // 4. FETCH NEXT OPCODE
            byte opcode = ReadNextByte();

            // 5. EXECUTE
            ExecuteOpcode(opcode);

            CheckInterrupts();

        }
        // Simple history tracker
        private List<string> pcHistory = new List<string>();

        // Helper to read a byte and push the PC forward automatically
        // Replace your old ReadNextByte with this:
        private byte ReadNextByte()
        {

            byte value = bus.ReadByte(PC);

            if (haltBugTriggered)
            {
                // HALT Bug: The PC does not increment, so the same byte 
                // will be read as the next opcode or data.
                haltBugTriggered = false;
            }
            else
            {
                PC++;
            }
            Tick(); 
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
                    Interrupt_on_Line = true;
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
                        ExecuteInterrupt(0, 0x40);
                    else if ((pendingInterrupts & 0x02) != 0)
                    { // Bit 1: LCD STAT
                        ExecuteInterrupt(1, 0x48);
                        
                    }
                    else if ((pendingInterrupts & 0x04) != 0) // Bit 2: Timer
                        ExecuteInterrupt(2, 0x50);
                    else if ((pendingInterrupts & 0x08) != 0) // Bit 3: Serial
                        ExecuteInterrupt(3, 0x58);

                    else if ((pendingInterrupts & 0x10) != 0) // Bit 4: Joypad
                        ExecuteInterrupt(4, 0x60);

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

            Interrupt_on_Line = false;


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
                    break;
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
                    WriteMemory(BC, A); // LD (BC), A
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
                case 0x08:
                    // --- LD (a16), SP ---

                    // 1. Fetch the 16-bit absolute address from the next two bytes (Little-Endian)
                    byte lowAddr = ReadNextByte();
                    byte highAddr = ReadNextByte();
                    ushort absoluteAddress = (ushort)((highAddr << 8) | lowAddr);

                    // 2. Extract the low and high bytes of the Stack Pointer
                    byte spLow = (byte)(SP & 0xFF);
                    byte spHigh = (byte)(SP >> 8);

                    // 3. Write the low byte to the address
                    WriteMemory(absoluteAddress, spLow);

                    // 4. Write the high byte to address + 1
                    // (Cast to ushort ensures the math wraps around 0xFFFF correctly)
                    WriteMemory((ushort)(absoluteAddress + 1), spHigh);

                    // Flags are completely unaffected by this operation!
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
                case 0x0A: // LD A, (DE)
                    A = ReadMemory(BC);
                    break;
                case 0x1A: // LD A, (DE)
                    A = ReadMemory(DE);
                    break;
                case 0x0B: // DEC DE
                    BC--;
                    Tick(); // 16-bit math delay
                    break;
                case 0x1B: // DEC DE
                    DE--;
                    Tick(); // 16-bit math delay
                    break;
                case 0x0C: // INC E
                    FlagH = (C & 0x0F) == 0x0F;
                    C++;
                    FlagZ = (C == 0); FlagN = false;
                    break;
                case 0x1C: // INC E
                    FlagH = (E & 0x0F) == 0x0F;
                    E++;
                    FlagZ = (E == 0); FlagN = false;
                    break;
                case 0x0D: // DEC E
                    C--;
                    FlagZ = (C == 0); FlagN = true;
                    FlagH = (C & 0x0F) == 0x0F;
                    break;
                case 0x1D: // DEC E
                    E--;
                    FlagZ = (E == 0); FlagN = true;
                    FlagH = (E & 0x0F) == 0x0F;
                    break;

                case 0x1E: // LD E, d8
                    E = ReadNextByte();
                    break;
                case 0x0F:
                    // --- RRCA (Rotate Right Circular Accumulator) ---

                    // 1. Grab Bit 0 to see if it carries over
                    bool carry0x0F = (A & 0x01) != 0;

                    // 2. Shift A right by 1, and wrap the carry bit around to Bit 7
                    A = (byte)((A >> 1) | (carry0x0F ? 0x80 : 0));

                    // 3. Update Flags (Notice that FlagZ is FORCED to false!)
                    FlagZ = false;
                    FlagN = false;
                    FlagH = false;
                    FlagC = carry0x0F;

                    // 4. Pad the execution time (4 T-Cycles)
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
                case 0x38: // JR C, r8 (Jump Relative if Zero)
                    sbyte offsets = (sbyte)ReadNextByte();
                    if (FlagC)
                    {
                        PC = (ushort)(PC + offsets);
                        Tick();
                    }
                    break;
                case 0x09: // ADD HL, HL
                    int results = HL + BC;
                    FlagN = false;
                    FlagH = ((HL & 0xFFF) + (BC & 0xFFF)) > 0xFFF;
                    FlagC = results > 0xFFFF;
                    HL = (ushort)results;
                    Tick();
                    break;
                case 0x29: // ADD HL, HL
                    int result29 = HL + HL;
                    FlagN = false;
                    FlagH = ((HL & 0xFFF) + (HL & 0xFFF)) > 0xFFF;
                    FlagC = result29 > 0xFFFF;
                    HL = (ushort)result29;
                    Tick();
                    break;
                case 0x39: // ADD HL, HL
                    int result39 = HL + SP;
                    FlagN = false;
                    FlagH = ((HL & 0xFFF) + (SP & 0xFFF)) > 0xFFF;
                    FlagC = result39 > 0xFFFF;
                    HL = (ushort)result39;
                    Tick();
                    break;

                case 0x2A: // LD A, (HL+) (Often written as LDI A, (HL))
                    // Load memory at HL into A, then instantly increment HL
                    A = ReadMemory(HL);
                    HL++;
                    break;

                case 0x3A: // LD A, (HL+) (Often written as LDI A, (HL))
                    // Load memory at HL into A, then instantly increment HL
                    A = ReadMemory(HL);
                    HL--;
                    break;

                case 0x2B: // DEC HL
                    HL--;
                    Tick();
                    break;
                case 0x3B: // DEC HL
                    SP--;
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
                case 0x3C: // INC L
                    FlagH = (A & 0x0F) == 0x0F;
                    A++;
                    FlagZ = (A == 0); FlagN = false;
                    break;
                case 0x3D:

                    FlagH = (A & 0x0F) == 0;


                    A--;
                    // 3. Update remaining flags
                    FlagZ = (A == 0);
                    FlagN = true; // Always true for DEC
                                  // FlagC is NOT affected
                    break;
                case 0x0E: // LD C, d8
                    C = ReadNextByte();
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
                case 0x3F:
                    // --- CCF (Complement Carry Flag) ---

                    // 1. Toggle the Carry flag
                    FlagC = !FlagC;

                    // 2. Clear N and H flags
                    FlagN = false;
                    FlagH = false;

                    // FlagZ remains unchanged
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
                    bool interruptPending = ((bus.ieRegister & bus.ReadByte(0xFF0F)) & 0x1F) != 0;

                    if (IME)
                    {
                        // Scenario 1: Normal Halt
                        Halted = true;
                    }
                    else
                    {
                        if (interruptPending)
                        {
                            // Scenario 3: THE HALT BUG
                            // IME is off, but an interrupt is already waiting.
                            // We don't halt, but the PC fails to increment for the NEXT fetch.
                            haltBugTriggered = true;
                        }
                        else
                        {
                            // Scenario 2: Warp Halt
                            Halted = true;
                        }
                    }
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
                // --- LOGICAL AND (AND A, register) ---
                case 0xA0: // AND A, B
                    AndA(B);
                    break;
                case 0xA1: // AND A, C
                    AndA(C);
                    break;
                case 0xA2: // AND A, D
                    AndA(D);
                    break;
                case 0xA3: // AND A, E
                    AndA(E);
                    break;
                case 0xA4: // AND A, H
                    AndA(H);
                    break;
                case 0xA5: // AND A, L
                    AndA(L);
                    break;
                case 0xA6: // AND A, (HL)
                    // Memory Accurate: 4 cycles (opcode) + 4 cycles (memory read) = 8 cycles
                    AndA(ReadMemory(HL));
                    break;
                case 0xA7: // AND A, A
                    AndA(A);
                    break;

                // --- LOGICAL XOR (XOR A, register) ---
                case 0xA8: // XOR A, B
                    XorA(B);
                    break;
                case 0xA9: // XOR A, C
                    XorA(C);
                    break;
                case 0xAA: // XOR A, D
                    XorA(D);
                    break;
                case 0xAB: // XOR A, E
                    XorA(E);
                    break;
                case 0xAC: // XOR A, H
                    XorA(H);
                    break;
                case 0xAD: // XOR A, L
                    XorA(L);
                    break;
                case 0xAE: // XOR A, (HL)
                    XorA(ReadMemory(HL));
                    break;
                case 0xAF: // XOR A, A (Fastest way to set A = 0)
                    XorA(A);
                    break;
                // --- LOGICAL OR (OR A, register) ---
                case 0xB0: // OR A, B
                    OrA(B);
                    break;
                case 0xB1: // OR A, C
                    OrA(C);
                    break;
                case 0xB2: // OR A, D
                    OrA(D);
                    break;
                case 0xB3: // OR A, E
                    OrA(E);
                    break;
                case 0xB4: // OR A, H
                    OrA(H);
                    break;
                case 0xB5: // OR A, L
                    OrA(L);
                    break;
                case 0xB6: // OR A, (HL)
                    // Memory Accurate: 4 cycles (opcode) + 4 cycles (memory read) = 8 cycles
                    OrA(ReadMemory(HL));
                    break;
                case 0xB7: // OR A, A
                    OrA(A); // ORing A with A doesn't change the value, but it perfectly updates flags!
                    break;

                // --- COMPARE (CP A, register) ---
                case 0xB8: // CP A, B
                    CpA(B);
                    break;
                case 0xB9: // CP A, C
                    CpA(C);
                    break;
                case 0xBA: // CP A, D
                    CpA(D);
                    break;
                case 0xBB: // CP A, E
                    CpA(E);
                    break;
                case 0xBC: // CP A, H
                    CpA(H);
                    break;
                case 0xBD: // CP A, L
                    CpA(L);
                    break;
                case 0xBE: // CP A, (HL)
                    CpA(ReadMemory(HL));
                    break;
                case 0xBF: // CP A, A
                    CpA(A); // This will always set the Zero flag (Z) to true, since A - A = 0
                    break;
                // --- RETURNS ---
                case 0xC0: // RET NZ (Return if Not Zero)
                    Tick(); // Branch decision delay (4 cycles)
                    if (!FlagZ)
                    {
                        PC = Pop16();
                        Tick(); // Post-pop delay (4 cycles)
                    }
                    break;
                case 0xC1: // POP BC
                    ushort valC1 = Pop16();
                    B = (byte)(valC1 >> 8);
                    C = (byte)(valC1 & 0xFF);
                    break;
                case 0xC8: // RET Z (Return if Zero)
                    Tick();
                    if (FlagZ)
                    {
                        PC = Pop16();
                        Tick();
                        return;
                    }

                    break;
                case 0xC9: // RET (Unconditional Return)

                    PC = Pop16();
                    //throw new Exception($"{PC}");
                    Tick();
                    break;

                // --- JUMPS ---
                case 0xC2: // JP NZ, a16 (Jump to 16-bit address if Not Zero)

                    if (!FlagZ)
                    {
                        ushort addrC2 = ReadNextWord();
                        PC = addrC2;
                        Tick();
                        return;// Internal delay to update the PC
                    }
                    PC += 2;
                    Tick();
                    Tick();
                    break;
                case 0xC3: // JP a16 (Unconditional Jump)
                    PC = ReadNextWord();
                    Tick();

                    break;
                case 0xCA: // JP Z, a16 (Jump to 16-bit address if Zero)

                    if (FlagZ)
                    {
                        ushort addrCA = ReadNextWord();
                        PC = addrCA;

                        Tick();
                        return;
                    }
                    PC += 2;
                    Tick();
                    Tick();
                    break;

                // --- CALLS & PUSHES ---
                case 0xC4: // CALL NZ, a16 (Call function if Not Zero)

                    if (!FlagZ)
                    {
                        ushort callAddrC4 = ReadNextWord();
                        Push16(PC); // Push the RETURN address to the stack


                        PC = callAddrC4;
                        return;

                    }
                    PC += 2;
                    Tick();
                    Tick();
                    break;
                case 0xC5: // PUSH BC
                    Push16((ushort)((B << 8) | C));
                    break;
                case 0xCC: // CALL Z, a16 (Call function if Zero)

                    if (FlagZ)
                    {
                        ushort callAddrCC = ReadNextWord();
                        Push16(PC);

                        PC = callAddrCC;
                        return;
                    }
                    PC += 2;
                    Tick();
                    Tick();
                    break;
                case 0xCD: // CALL a16 (Unconditional Call)
                    ushort callAddrCD = ReadNextWord();
                    Push16(PC);
                    PC = (ushort)(callAddrCD) ;

                    break;

                // --- IMMEDIATE MATH ---
                case 0xC6: // ADD A, d8 (Add immediate 8-bit value to A)
                    // Memory Accurate: 4 (opcode) + 4 (read byte) = 8 cycles
                    AddA(ReadNextByte());
                    break;
                case 0xCE: // ADC A, d8 (Add immediate 8-bit value + Carry to A)
                    AdcA(ReadNextByte());
                    break;

                // --- RESTARTS (Hardcoded Calls) ---
                case 0xC7: // RST 00H (Call address 0x0000)
                    Push16(PC);
                    PC = 0x0000;
                    break;
                case 0xCF: // RST 08H (Call address 0x0008)
                    Push16(PC);
                    PC = 0x0008;
                    break;

                case 0xCB: // PREFIX CB (Extended Instructions)
                    ExecuteCbOpcode(ReadNextByte());
                    break;
                // --- RETURNS ---
                case 0xD0: // RET NC (Return if No Carry)
                    Tick(); // Branch decision delay (4 cycles)
                    if (!FlagC)
                    {
                        PC = Pop16();
                        Tick(); // Post-pop delay
                        return;
                    }
                    break;
                case 0xD1: // POP DE
                    ushort valD1 = Pop16();
                    D = (byte)(valD1 >> 8);
                    E = (byte)(valD1 & 0xFF);
                    break;
                case 0xD8: // RET C (Return if Carry)
                    Tick();
                    if (FlagC)
                    {
                        PC = Pop16();
                        Tick();
                        return;
                    }
                    break;
                case 0xD9: // RETI (Return and Enable Interrupts)
                    PC = Pop16();
                    Tick();
                    IME = true; // Turn the master interrupt switch back on!
                    break;

                // --- JUMPS ---
                case 0xD2: // JP NC, a16 (Jump if No Carry)

                    if (!FlagC)
                    {
                        ushort addrD2 = ReadNextWord();
                        PC = addrD2;
                        Tick();
                        return;
                    }
                    PC += 2;
                    Tick();
                    Tick();
                    break;
                case 0xDA: // JP C, a16 (Jump if Carry)

                    if (FlagC)
                    {
                        ushort addrDA = ReadNextWord();
                        PC = addrDA;
                        Tick();
                        return;
                    }
                    PC += 2;
                    Tick();
                    Tick();
                    break;

                // --- CALLS & PUSHES ---
                case 0xD4: // CALL NC, a16 (Call if No Carry)

                    if (!FlagC)
                    {
                        ushort callAddrD4 = ReadNextWord();
                        Push16(PC);
                        PC = callAddrD4;
                        return;
                    }
                    PC += 2;
                    Tick();
                    Tick();
                    break;
                case 0xD5: // PUSH DE
                    Push16((ushort)((D << 8) | E));
                    break;
                case 0xDC: // CALL C, a16 (Call if Carry)

                    if (FlagC)
                    {
                        ushort callAddrDC = ReadNextWord();
                        Push16(PC);
                        PC = callAddrDC;
                        return;
                    }
                    PC += 2;
                    Tick();
                    Tick();
                    break;

                // --- IMMEDIATE MATH ---
                case 0xD6: // SUB A, d8 (Subtract immediate 8-bit value from A)
                    SubA(ReadNextByte());
                    break;
                case 0xDE: // SBC A, d8 (Subtract immediate + Carry from A)
                    SbcA(ReadNextByte());
                    break;

                // --- RESTARTS (Hardcoded Calls) ---
                case 0xD7: // RST 10H (Call address 0x0010)
                    Push16(PC);
                    PC = 0x0010;
                    break;
                case 0xDF: // RST 18H (Call address 0x0018)
                    Push16(PC);
                    PC = 0x0018;
                    break;
                // --- HIGH RAM (HRAM) LOADS ---
                case 0xE0: // LDH (a8), A  (Store A into 0xFF00 + 8-bit offset)
                    WriteMemory((ushort)(0xFF00 + ReadNextByte()), A);
                    break;
                case 0xE2: // LDH (C), A   (Store A into 0xFF00 + register C)
                    // Memory Accurate: 4 (opcode) + 4 (write) = 8 cycles
                    WriteMemory((ushort)(0xFF00 + C), A);
                    break;
                case 0xEA: // LD (a16), A  (Standard 16-bit Absolute Store)
                    WriteMemory(ReadNextWord(), A);
                    break;

                // --- STACK POINTER / HL INSTRUCTIONS ---
                case 0xE1: // POP HL
                    ushort valE1 = Pop16();
                    H = (byte)(valE1 >> 8);
                    L = (byte)(valE1 & 0xFF);
                    break;
                case 0xE5: // PUSH HL
                    Push16((ushort)((H << 8) | L));
                    break;
                case 0xE8: // ADD SP, r8 (Add signed 8-bit value to Stack Pointer)
                    sbyte offsetE8 = (sbyte)ReadNextByte();
                    SP = AddSignedByteToSP(offsetE8);
                    Tick(); // Internal Delay 1
                    Tick(); // Internal Delay 2 (Total 16 T-Cycles)
                    break;
                case 0xE9: // JP (HL) (Jump to the address stored in HL)
                    // The syntax is JP (HL) but it actually sets PC = HL instantly!
                    PC = (ushort)((H << 8) | L);
                    // Takes exactly 4 T-Cycles, which is already handled by the opcode fetch
                    break;

                // --- IMMEDIATE MATH ---
                case 0xE6: // AND A, d8 (Logical AND with immediate 8-bit value)
                    AndA(ReadNextByte());
                    break;
                case 0xEE: // XOR A, d8 (Logical XOR with immediate 8-bit value)
                    XorA(ReadNextByte());
                    break;

                // --- RESTARTS (Hardcoded Calls) ---
                case 0xE7: // RST 20H (Call address 0x0020)
                    Push16(PC);
                    PC = 0x0020;
                    break;
                case 0xEF: // RST 28H (Call address 0x0028)
                    Push16(PC);
                    PC = 0x0028;
                    break;
                // --- HIGH RAM (HRAM) READS ---
                case 0xF0: // LDH A, (a8)  (Load A from 0xFF00 + 8-bit offset)
                    //throw new Exception($"{ReadMemory((ushort)(0xFF00 + ReadNextByte()))},{bus.ppu.LY}");
                    A = ReadMemory((ushort)(0xFF00 + ReadNextByte()));
                    break;
                case 0xF2: // LDH A, (C)   (Load A from 0xFF00 + register C)
                    A = ReadMemory((ushort)(0xFF00 + C));
                    break;
                case 0xFA: // LD A, (a16)  (Standard 16-bit Absolute Load)
                    A = ReadMemory(ReadNextWord());
                    break;

                // --- STACK & HL INSTRUCTIONS ---
                case 0xF1: // POP AF
                    ushort valF1 = Pop16();
                    A = (byte)(valF1 >> 8);
                    // HARDWARE QUIRK: The bottom 4 bits of the F register are hardwired to 0!
                    F = (byte)(valF1 & 0xF0);
                    break;
                case 0xF5: // PUSH AF
                    Push16((ushort)((A << 8) | F));
                    break;
                case 0xF8: // LD HL, SP+r8 (Add signed 8-bit value to SP, store in HL)
                    sbyte offsetF8 = (sbyte)ReadNextByte();
                    ushort resultF8 = AddSignedByteToSP(offsetF8);
                    H = (byte)(resultF8 >> 8);
                    L = (byte)(resultF8 & 0xFF);
                    Tick(); // Internal Delay (Total 12 T-Cycles)
                    break;
                case 0xF9: // LD SP, HL (Load HL into the Stack Pointer)
                    SP = (ushort)((H << 8) | L);
                    Tick(); // Internal Delay (Total 8 T-Cycles)
                    break;

                // --- MASTER INTERRUPT SWITCHES ---
                case 0xF3: // DI (Disable Interrupts)
                    // The CPU will no longer jump to 0x0040, etc., even if hardware requests it
                    IME = false;
                    break;
                case 0xFB: // EI (Enable Interrupts)
                    // (Note: In pure hardware, this actually enables interrupts AFTER the next 
                    // instruction runs, but setting it instantly works for 99% of games!)
                    IME = true;
                    break;

                // --- IMMEDIATE MATH ---
                case 0xF6: // OR A, d8 (Logical OR with immediate 8-bit value)
                    OrA(ReadNextByte());
                    break;
                case 0xFE: // CP A, d8 (Compare A with immediate 8-bit value)
                    CpA(ReadNextByte());
                    break;

                // --- RESTARTS (Hardcoded Calls) ---
                case 0xF7: // RST 30H (Call address 0x0030)
                    Push16(PC);
                    PC = 0x0030;
                    break;
                case 0xFF: // RST 38H (Call address 0x0038)
                    Push16(PC);
                    PC = 0x0038;
                    break;
                case 0x10:
                    return;

                default:
                    throw new NotImplementedException($"Opcode 0x{opcode:X2} at PC 0x{PC - 1:X4} is not implemented!");
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
            FlagH = (((A & 0x0F) + (value & 0x0F) + carry) > 0x0F);
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
        private void AndA(byte value)
        {
            // Perform the bitwise AND
            A &= value;

            // Update Flags
            FlagZ = (A == 0);
            FlagN = false;
            FlagH = true;  // Game Boy hardware quirk: AND ALWAYS sets the H flag!
            FlagC = false;
        }

        private void XorA(byte value)
        {
            // Perform the bitwise Exclusive OR
            A ^= value;

            // Update Flags
            FlagZ = (A == 0);
            FlagN = false;
            FlagH = false;
            FlagC = false;
        }
        private void OrA(byte value)
        {
            // Perform the bitwise OR
            A |= value;

            // Update Flags
            FlagZ = (A == 0);
            FlagN = false;
            FlagH = false; // Unlike AND, OR clears the Half-Carry flag
            FlagC = false;
        }

        private void CpA(byte value)
        {
            // Compare is exactly the same as Subtraction, but we DON'T save to A!
            int result = A - value;

            // Update Flags
            FlagZ = ((result & 0xFF) == 0);
            FlagN = true; // Set to true because this is a subtraction operation
            int lowerA = A & 0x0F;
            int lowerValue = value & 0x0F;
            if ((lowerA - lowerValue < 0x00) && (lowerA > 0x8 || lowerValue > 0x8))
            {
                FlagH = true;
            }
            else
            {
                FlagH = false;
                if (lowerA - lowerValue < 0x00)
                {
                    FlagH = true;
                }
            }

            FlagC = (A < value); // Check for full borrow
        }
        // --- STACK HELPER METHODS ---
        private void Push16(ushort value)
        {
            Tick(); // Internal delay: CPU takes 4 cycles to prep for a push CHANGE LATER

            // Push the higher byte first
            SP--;
            WriteMemory(SP, (byte)(value >> 8));

            // Push the lower byte second
            SP--;
            WriteMemory(SP, (byte)(value & 0xFF));
        }

        private ushort Pop16()
        {
            // Pop the lower byte first
            byte lo = ReadMemory(SP);
            SP++;

            // Pop the higher byte second
            byte hi = ReadMemory(SP);
            SP++;

            return (ushort)((hi << 8) | lo);
        }
        private ushort AddSignedByteToSP(sbyte value)
        {
            // The Game Boy calculates the Half-Carry and Carry flags for this 16-bit 
            // instruction by looking ONLY at the lower 8-bits, treating them as unsigned!
            int result = SP + value;

            FlagZ = false;
            FlagN = false;

            // Half-carry checks if bits 0-3 overflowed
            FlagH = ((SP & 0x0F) + (value & 0x0F) > 0x0F);

            // Full carry checks if bits 0-7 overflowed
            FlagC = ((SP & 0xFF) + (value & 0xFF) > 0xFF);

            return (ushort)result;
        }
        private void ExecuteCbOpcode(byte cbOpcode)
        {
            // The CB prefix gives access to 256 MORE instructions (Bit shifting, setting, testing)
            switch (cbOpcode)
            {
                case 0x00:
                    B = Rlc(B);
                    break;
                case 0x01:
                    C = Rlc(C);
                    break;
                case 0x02:
                    D = Rlc(D);
                    break;
                case 0x03:
                    E = Rlc(E);
                    break;
                case 0x04:
                    H = Rlc(H);
                    break;
                case 0x05:
                    L = Rlc(L);
                    break;
                case 0x06:
                    WriteMemory(HL, Rlc(ReadMemory(HL)));
                    break;
                case 0x07:
                    // HARDWARE QUIRK: This is CB 0x07 (RLC A). It is identical to the main board's 
                    // 0x07 (RLCA), EXCEPT this CB version sets the Zero flag if A is 0, while 
                    // the main board version always forces the Zero flag to false!
                    A = Rlc(A);
                    break;

                // --- RRC (Rotate Right Circular) ---
                case 0x08:
                    B = Rrc(B);
                    break;
                case 0x09:
                    C = Rrc(C);
                    break;
                case 0x0A:
                    D = Rrc(D);
                    break;
                case 0x0B:
                    E = Rrc(E);
                    break;
                case 0x0C:
                    H = Rrc(H);
                    break;
                case 0x0D:
                    L = Rrc(L);
                    break;
                case 0x0E:
                    WriteMemory(HL, Rrc(ReadMemory(HL)));
                    break;
                case 0x0F:
                    A = Rrc(A);
                    break;
                // --- RL (Rotate Left Through Carry) ---
                case 0x10:
                    B = Rl(B);
                    break;
                case 0x11:
                    C = Rl(C);
                    break;
                case 0x12:
                    D = Rl(D);
                    break;
                case 0x13:
                    E = Rl(E);
                    break;
                case 0x14:
                    H = Rl(H);
                    break;
                case 0x15:
                    L = Rl(L);
                    break;
                case 0x16:
                    WriteMemory(HL, Rl(ReadMemory(HL)));
                    break;
                case 0x17:
                    A = Rl(A);
                    break;

                // --- RR (Rotate Right Through Carry) ---
                case 0x18:
                    B = Rr(B);
                    break;
                case 0x19:
                    C = Rr(C);
                    break;
                case 0x1A:
                    D = Rr(D);
                    break;
                case 0x1B:
                    E = Rr(E);
                    break;
                case 0x1C:
                    H = Rr(H);
                    break;
                case 0x1D:
                    L = Rr(L);
                    break;
                case 0x1E:
                    WriteMemory(HL, Rr(ReadMemory(HL)));
                    break;
                case 0x1F:
                    A = Rr(A);
                    break;
                // --- SLA (Shift Left Arithmetic) ---
                case 0x20:
                    B = Sla(B);
                    break;
                case 0x21:
                    C = Sla(C);
                    break;
                case 0x22:
                    D = Sla(D);
                    break;
                case 0x23:
                    E = Sla(E);
                    break;
                case 0x24:
                    H = Sla(H);
                    break;
                case 0x25:
                    L = Sla(L);
                    break;
                case 0x26:
                    WriteMemory(HL, Sla(ReadMemory(HL)));
                    break;
                case 0x27:
                    A = Sla(A);
                    break;

                // --- SRA (Shift Right Arithmetic) ---
                case 0x28:
                    B = Sra(B);
                    break;
                case 0x29:
                    C = Sra(C);
                    break;
                case 0x2A:
                    D = Sra(D);
                    break;
                case 0x2B:
                    E = Sra(E);
                    break;
                case 0x2C:
                    H = Sra(H);
                    break;
                case 0x2D:
                    L = Sra(L);
                    break;
                case 0x2E:
                    WriteMemory(HL, Sra(ReadMemory(HL)));
                    break;
                case 0x2F:
                    A = Sra(A);
                    break;
                // --- SWAP (Swap Nibbles) ---
                case 0x30:
                    B = Swap(B);
                    break;
                case 0x31:
                    C = Swap(C);
                    break;
                case 0x32:
                    D = Swap(D);
                    break;
                case 0x33:
                    E = Swap(E);
                    break;
                case 0x34:
                    H = Swap(H);
                    break;
                case 0x35:
                    L = Swap(L);
                    break;
                case 0x36:
                    WriteMemory(HL, Swap(ReadMemory(HL)));
                    break;
                case 0x37:
                    A = Swap(A);
                    break;

                // --- SRL (Shift Right Logical) ---
                case 0x38:
                    B = Srl(B);
                    break;
                case 0x39:
                    C = Srl(C);
                    break;
                case 0x3A:
                    D = Srl(D);
                    break;
                case 0x3B:
                    E = Srl(E);
                    break;
                case 0x3C:
                    H = Srl(H);
                    break;
                case 0x3D:
                    L = Srl(L);
                    break;
                case 0x3E:
                    WriteMemory(HL, Srl(ReadMemory(HL)));
                    break;
                case 0x3F:
                    A = Srl(A);
                    break;
                // --- BIT 0 (Test Bit 0) ---
                case 0x40:
                    TestBit(B, 0);
                    break;
                case 0x41:
                    TestBit(C, 0);
                    break;
                case 0x42:
                    TestBit(D, 0);
                    break;
                case 0x43:
                    TestBit(E, 0);
                    break;
                case 0x44:
                    TestBit(H, 0);
                    break;
                case 0x45:
                    TestBit(L, 0);
                    break;
                case 0x46:
                    // Memory Accurate: 4 (CB prefix) + 4 (opcode) + 4 (read) = 12 cycles
                    TestBit(ReadMemory(HL), 0);
                    break;
                case 0x47: TestBit(A, 0); break;

                // --- BIT 1 (Test Bit 1) ---
                case 0x48:
                    TestBit(B, 1);
                    break;
                case 0x49:
                    TestBit(C, 1);
                    break;
                case 0x4A:
                    TestBit(D, 1);
                    break;
                case 0x4B:
                    TestBit(E, 1);
                    break;
                case 0x4C:
                    TestBit(H, 1);
                    break;
                case 0x4D:
                    TestBit(L, 1);
                    break;
                case 0x4E:
                    TestBit(ReadMemory(HL), 1);
                    break;
                case 0x4F:
                    TestBit(A, 1);
                    break;
                // ==========================================
                // ======= FINISHING BIT OPERATIONS =========
                // ==========================================

                // --- BIT 2 ---
                case 0x50:
                    TestBit(B, 2);
                    break;
                case 0x51:
                    TestBit(C, 2);
                    break;
                case 0x52:
                    TestBit(D, 2);
                    break;
                case 0x53:
                    TestBit(E, 2);
                    break;
                case 0x54:
                    TestBit(H, 2);
                    break;
                case 0x55:
                    TestBit(L, 2);
                    break;
                case 0x56:
                    TestBit(ReadMemory(HL), 2);
                    break;
                case 0x57:
                    TestBit(A, 2);
                    break;

                // --- BIT 3 ---
                case 0x58:
                    TestBit(B, 3);
                    break;
                case 0x59:
                    TestBit(C, 3);
                    break;
                case 0x5A:
                    TestBit(D, 3);
                    break;
                case 0x5B:
                    TestBit(E, 3);
                    break;
                case 0x5C:
                    TestBit(H, 3);
                    break;
                case 0x5D:
                    TestBit(L, 3);
                    break;
                case 0x5E:
                    TestBit(ReadMemory(HL), 3);
                    break;
                case 0x5F:
                    TestBit(A, 3);
                    break;

                // --- BIT 4 ---
                case 0x60:
                    TestBit(B, 4);
                    break;
                case 0x61:
                    TestBit(C, 4);
                    break;
                case 0x62:
                    TestBit(D, 4);
                    break;
                case 0x63:
                    TestBit(E, 4);
                    break;
                case 0x64:
                    TestBit(H, 4);
                    break;
                case 0x65:
                    TestBit(L, 4);
                    break;
                case 0x66:
                    TestBit(ReadMemory(HL), 4);
                    break;
                case 0x67:
                    TestBit(A, 4);
                    break;

                // --- BIT 5 ---
                case 0x68:
                    TestBit(B, 5); break;
                case 0x69:
                    TestBit(C, 5); break;
                case 0x6A:
                    TestBit(D, 5); break;
                case 0x6B:
                    TestBit(E, 5); break;
                case 0x6C:
                    TestBit(H, 5); break;
                case 0x6D:
                    TestBit(L, 5); break;
                case 0x6E:
                    TestBit(ReadMemory(HL), 5); break;
                case 0x6F:
                    TestBit(A, 5); break;

                // --- BIT 6 ---
                case 0x70:
                    TestBit(B, 6); break;
                case 0x71:
                    TestBit(C, 6); break;
                case 0x72:
                    TestBit(D, 6); break;
                case 0x73:
                    TestBit(E, 6); break;
                case 0x74:
                    TestBit(H, 6); break;
                case 0x75:
                    TestBit(L, 6); break;
                case 0x76:
                    TestBit(ReadMemory(HL), 6); break;
                case 0x77:
                    TestBit(A, 6); break;

                // --- BIT 7 ---
                case 0x78:
                    TestBit(B, 7); break;
                case 0x79:
                    TestBit(C, 7); break;
                case 0x7A:
                    TestBit(D, 7); break;
                case 0x7B:
                    TestBit(E, 7); break;
                case 0x7C:
                    TestBit(H, 7); break;
                case 0x7D:
                    TestBit(L, 7); break;
                case 0x7E:
                    TestBit(ReadMemory(HL), 7); break;
                case 0x7F:
                    TestBit(A, 7); break;

                // ==========================================
                // ========= RESET (RES) OPERATIONS =========
                // ==========================================

                // --- RES 0 (Reset Bit 0) ---
                case 0x80:
                    B = ResetBit(B, 0);
                    break;
                case 0x81:
                    C = ResetBit(C, 0);
                    break;
                case 0x82:
                    D = ResetBit(D, 0); break;
                case 0x83:
                    E = ResetBit(E, 0);
                    break;
                case 0x84:
                    H = ResetBit(H, 0);
                    break;
                case 0x85:
                    L = ResetBit(L, 0);
                    break;
                case 0x86:
                    WriteMemory(HL, ResetBit(ReadMemory(HL), 0));
                    break;
                case 0x87:
                    A = ResetBit(A, 0);
                    break;

                // --- RES 1 (Reset Bit 1) ---
                case 0x88:
                    B = ResetBit(B, 1);
                    break;
                case 0x89:
                    C = ResetBit(C, 1);
                    break;
                case 0x8A:
                    D = ResetBit(D, 1);
                    break;
                case 0x8B:
                    E = ResetBit(E, 1);
                    break;
                case 0x8C:
                    H = ResetBit(H, 1);
                    break;
                case 0x8D:
                    L = ResetBit(L, 1);
                    break;
                case 0x8E:
                    WriteMemory(HL, ResetBit(ReadMemory(HL), 1));
                    break;
                case 0x8F:
                    A = ResetBit(A, 1);
                    break;

                // --- RES 2 (Reset Bit 2) ---
                case 0x90:
                    B = ResetBit(B, 2);
                    break;
                case 0x91:
                    C = ResetBit(C, 2);
                    break;
                case 0x92:
                    D = ResetBit(D, 2);
                    break;
                case 0x93:
                    E = ResetBit(E, 2);
                    break;
                case 0x94:
                    H = ResetBit(H, 2);
                    break;
                case 0x95:
                    L = ResetBit(L, 2); break;
                case 0x96:
                    WriteMemory(HL, ResetBit(ReadMemory(HL), 2));
                    break;
                case 0x97:
                    A = ResetBit(A, 2);
                    break;

                // --- RES 3 (Reset Bit 3) ---
                case 0x98:
                    B = ResetBit(B, 3);
                    break;
                case 0x99:
                    C = ResetBit(C, 3);
                    break;
                case 0x9A:
                    D = ResetBit(D, 3);
                    break;
                case 0x9B:
                    E = ResetBit(E, 3);
                    break;
                case 0x9C:
                    H = ResetBit(H, 3);
                    break;
                case 0x9D:
                    L = ResetBit(L, 3);
                    break;
                case 0x9E:
                    WriteMemory(HL, ResetBit(ReadMemory(HL), 3));
                    break;
                case 0x9F:
                    A = ResetBit(A, 3);
                    break;
                // ==========================================
                // ========= FINISHING RES BITS 4-7 =========
                // ==========================================

                // --- RES 4 ---
                case 0xA0: B = ResetBit(B, 4); break;
                case 0xA1: C = ResetBit(C, 4); break;
                case 0xA2: D = ResetBit(D, 4); break;
                case 0xA3: E = ResetBit(E, 4); break;
                case 0xA4: H = ResetBit(H, 4); break;
                case 0xA5: L = ResetBit(L, 4); break;
                case 0xA6: WriteMemory(HL, ResetBit(ReadMemory(HL), 4)); break;
                case 0xA7: A = ResetBit(A, 4); break;

                // --- RES 5 ---
                case 0xA8: B = ResetBit(B, 5); break;
                case 0xA9: C = ResetBit(C, 5); break;
                case 0xAA: D = ResetBit(D, 5); break;
                case 0xAB: E = ResetBit(E, 5); break;
                case 0xAC: H = ResetBit(H, 5); break;
                case 0xAD: L = ResetBit(L, 5); break;
                case 0xAE: WriteMemory(HL, ResetBit(ReadMemory(HL), 5)); break;
                case 0xAF: A = ResetBit(A, 5); break;

                // --- RES 6 ---
                case 0xB0: B = ResetBit(B, 6); break;
                case 0xB1: C = ResetBit(C, 6); break;
                case 0xB2: D = ResetBit(D, 6); break;
                case 0xB3: E = ResetBit(E, 6); break;
                case 0xB4: H = ResetBit(H, 6); break;
                case 0xB5: L = ResetBit(L, 6); break;
                case 0xB6: WriteMemory(HL, ResetBit(ReadMemory(HL), 6)); break;
                case 0xB7: A = ResetBit(A, 6); break;

                // --- RES 7 ---
                case 0xB8: B = ResetBit(B, 7); break;
                case 0xB9: C = ResetBit(C, 7); break;
                case 0xBA: D = ResetBit(D, 7); break;
                case 0xBB: E = ResetBit(E, 7); break;
                case 0xBC: H = ResetBit(H, 7); break;
                case 0xBD: L = ResetBit(L, 7); break;
                case 0xBE: WriteMemory(HL, ResetBit(ReadMemory(HL), 7)); break;
                case 0xBF: A = ResetBit(A, 7); break;

                // ==========================================
                // ========= SET (SET) OPERATIONS ===========
                // ==========================================

                // --- SET 0 ---
                case 0xC0: B = SetBit(B, 0); break;
                case 0xC1: C = SetBit(C, 0); break;
                case 0xC2: D = SetBit(D, 0); break;
                case 0xC3: E = SetBit(E, 0); break;
                case 0xC4: H = SetBit(H, 0); break;
                case 0xC5: L = SetBit(L, 0); break;
                case 0xC6: WriteMemory(HL, SetBit(ReadMemory(HL), 0)); break;
                case 0xC7: A = SetBit(A, 0); break;

                // --- SET 1 ---
                case 0xC8: B = SetBit(B, 1); break;
                case 0xC9: C = SetBit(C, 1); break;
                case 0xCA: D = SetBit(D, 1); break;
                case 0xCB: E = SetBit(E, 1); break;
                case 0xCC: H = SetBit(H, 1); break;
                case 0xCD: L = SetBit(L, 1); break;
                case 0xCE: WriteMemory(HL, SetBit(ReadMemory(HL), 1)); break;
                case 0xCF: A = SetBit(A, 1); break;

                // --- SET 2 ---
                case 0xD0: B = SetBit(B, 2); break;
                case 0xD1: C = SetBit(C, 2); break;
                case 0xD2: D = SetBit(D, 2); break;
                case 0xD3: E = SetBit(E, 2); break;
                case 0xD4: H = SetBit(H, 2); break;
                case 0xD5: L = SetBit(L, 2); break;
                case 0xD6: WriteMemory(HL, SetBit(ReadMemory(HL), 2)); break;
                case 0xD7: A = SetBit(A, 2); break;

                // --- SET 3 ---
                case 0xD8: B = SetBit(B, 3); break;
                case 0xD9: C = SetBit(C, 3); break;
                case 0xDA: D = SetBit(D, 3); break;
                case 0xDB: E = SetBit(E, 3); break;
                case 0xDC: H = SetBit(H, 3); break;
                case 0xDD: L = SetBit(L, 3); break;
                case 0xDE: WriteMemory(HL, SetBit(ReadMemory(HL), 3)); break;
                case 0xDF: A = SetBit(A, 3); break;

                // --- SET 4 ---
                case 0xE0: B = SetBit(B, 4); break;
                case 0xE1: C = SetBit(C, 4); break;
                case 0xE2: D = SetBit(D, 4); break;
                case 0xE3: E = SetBit(E, 4); break;
                case 0xE4: H = SetBit(H, 4); break;
                case 0xE5: L = SetBit(L, 4); break;
                case 0xE6: WriteMemory(HL, SetBit(ReadMemory(HL), 4)); break;
                case 0xE7: A = SetBit(A, 4); break;

                // --- SET 5 ---
                case 0xE8: B = SetBit(B, 5); break;
                case 0xE9: C = SetBit(C, 5); break;
                case 0xEA: D = SetBit(D, 5); break;
                case 0xEB: E = SetBit(E, 5); break;
                case 0xEC: H = SetBit(H, 5); break;
                case 0xED: L = SetBit(L, 5); break;
                case 0xEE: WriteMemory(HL, SetBit(ReadMemory(HL), 5)); break;
                case 0xEF: A = SetBit(A, 5); break;

                // --- SET 6 ---
                case 0xF0: B = SetBit(B, 6); break;
                case 0xF1: C = SetBit(C, 6); break;
                case 0xF2: D = SetBit(D, 6); break;
                case 0xF3: E = SetBit(E, 6); break;
                case 0xF4: H = SetBit(H, 6); break;
                case 0xF5: L = SetBit(L, 6); break;
                case 0xF6: WriteMemory(HL, SetBit(ReadMemory(HL), 6)); break;
                case 0xF7: A = SetBit(A, 6); break;

                // --- SET 7 ---
                case 0xF8: B = SetBit(B, 7); break;
                case 0xF9: C = SetBit(C, 7); break;
                case 0xFA: D = SetBit(D, 7); break;
                case 0xFB: E = SetBit(E, 7); break;
                case 0xFC: H = SetBit(H, 7); break;
                case 0xFD: L = SetBit(L, 7); break;
                case 0xFE: WriteMemory(HL, SetBit(ReadMemory(HL), 7)); break;
                case 0xFF: A = SetBit(A, 7); break;



                default:
                    throw new NotImplementedException($"CB Opcode 0x{cbOpcode:X2} at PC 0x{PC - 2:X4} is not implemented!");
            }
        }
        private byte Rlc(byte value)
        {
            // 1. Grab the top bit (Bit 7) to see if it carries over
            bool carry = (value & 0x80) != 0;

            // 2. Shift left by 1, and if carry is true, wrap a 1 into the bottom bit (Bit 0)
            byte result = (byte)((value << 1) | (carry ? 1 : 0));

            // 3. Update flags
            FlagZ = (result == 0);
            FlagN = false;
            FlagH = false;
            FlagC = carry;

            return result;
        }

        private byte Rrc(byte value)
        {
            // 1. Grab the bottom bit (Bit 0) to see if it carries over
            bool carry = (value & 0x01) != 0;

            // 2. Shift right by 1, and if carry is true, wrap a 1 into the top bit (Bit 7)
            byte result = (byte)((value >> 1) | (carry ? 0x80 : 0));

            // 3. Update flags
            FlagZ = (result == 0);
            FlagN = false;
            FlagH = false;
            FlagC = carry;

            return result;
        }
        private byte Rl(byte value)
        {
            // 1. Save the old carry flag (this will become the new Bit 0)
            bool oldCarry = FlagC;

            // 2. Grab the top bit (Bit 7) to become the NEW carry flag
            bool newCarry = (value & 0x80) != 0;

            // 3. Shift left by 1, and insert the old carry into the bottom bit
            byte result = (byte)((value << 1) | (oldCarry ? 1 : 0));

            // 4. Update flags
            FlagZ = (result == 0);
            FlagN = false;
            FlagH = false;
            FlagC = newCarry;

            return result;
        }

        private byte Rr(byte value)
        {
            // 1. Save the old carry flag (this will become the new Bit 7)
            bool oldCarry = FlagC;

            // 2. Grab the bottom bit (Bit 0) to become the NEW carry flag
            bool newCarry = (value & 0x01) != 0;

            // 3. Shift right by 1, and insert the old carry into the top bit
            byte result = (byte)((value >> 1) | (oldCarry ? 0x80 : 0));

            // 4. Update flags
            FlagZ = (result == 0);
            FlagN = false;
            FlagH = false;
            FlagC = newCarry;

            return result;
        }
        private byte Sla(byte value)
        {
            // 1. Grab the top bit to put into the Carry flag
            bool carry = (value & 0x80) != 0;

            // 2. Shift left by 1. C# naturally fills the empty Bit 0 with a zero.
            byte result = (byte)(value << 1);

            // 3. Update flags
            FlagZ = (result == 0);
            FlagN = false;
            FlagH = false;
            FlagC = carry;

            return result;
        }
        private byte Sra(byte value)
        {
            // 1. Grab the bottom bit to put into the Carry flag
            bool carry = (value & 0x01) != 0;

            // 2. Save the original Bit 7 (the sign bit) so we can put it back
            byte topBit = (byte)(value & 0x80);

            // 3. Shift right by 1, then use bitwise OR to force the top bit to stay the same
            byte result = (byte)((value >> 1) | topBit);

            // 4. Update flags
            FlagZ = (result == 0);
            FlagN = false;
            FlagH = false;
            FlagC = carry;

            return result;
        }
        private byte Swap(byte value)
        {
            // 1. Shift the top 4 bits down, and the bottom 4 bits up
            byte upperNibble = (byte)(value >> 4);
            byte lowerNibble = (byte)(value << 4);

            // 2. Combine them back together
            byte result = (byte)(upperNibble | lowerNibble);

            // 3. Update flags (Swap wipes out all flags except Zero!)
            FlagZ = (result == 0);
            FlagN = false;
            FlagH = false;
            FlagC = false;

            return result;
        }
        private byte Srl(byte value)
        {
            // 1. Grab the bottom bit to put into the Carry flag
            bool carry = (value & 0x01) != 0;

            // 2. Shift right by 1. Because C# treats bytes as unsigned, 
            // the empty Bit 7 space is automatically filled with a 0.
            byte result = (byte)(value >> 1);

            // 3. Update flags
            FlagZ = (result == 0);
            FlagN = false;
            FlagH = false;
            FlagC = carry;

            return result;
        }
        private void TestBit(byte value, int bitPosition)
        {
            // 1. Create a mask for the specific bit (e.g., 1 << 0 is 0000 0001)
            int mask = 1 << bitPosition;

            // 2. Use bitwise AND to isolate the bit. If the result is 0, the bit was 0!
            bool isBitZero = (value & mask) == 0;

            // 3. Update flags
            FlagZ = isBitZero;
            FlagN = false;
            FlagH = true; // Hardware quirk: BIT always sets the Half-Carry flag

            // FlagC remains unchanged!
        }
        private byte ResetBit(byte value, int bitPosition)
        {
            // 1. Create a mask for the specific bit (e.g., 1 << 0 is 0000 0001)
            // 2. Flip it with ~ so it becomes 1111 1110
            int mask = ~(1 << bitPosition);

            // 3. Use bitwise AND. The 0 forces the target bit to turn off, 
            // while the 1s leave all other bits perfectly intact!

            return (byte)(value & mask);
        }
        private byte SetBit(byte value, int bitPosition)
        {
            // 1. Create a mask for the specific bit (e.g., 1 << 0 is 0000 0001)
            int mask = 1 << bitPosition;

            // 2. Use bitwise OR. The 0s in the mask leave the original bits intact, 
            // but the 1 forces the target bit to turn on!
            return (byte)(value | mask);
        }
    }

}