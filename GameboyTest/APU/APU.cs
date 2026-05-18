using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NAudio.Wave;

namespace GameboyTest.NewFolder
{
    public class APU
    {
        private WaveOutEvent waveOut;
        private BufferedWaveProvider waveProvider;
        private const int SAMPLE_RATE = 44100;
        private double time = 0;

        private int apuCycles = 0;
        private int frameSequencerStep = 0;

        // --- MASTER CONTROL REGISTERS (STEREO & POWER) ---
        public byte NR50, NR51, NR52;

        // --- CHANNEL REGISTERS & STATE ---
        public byte NR10, NR11, NR12, NR13, NR14;
        public bool ch1IsPlaying = false;
        private int ch1CurrentVolume = 0;
        private int ch1EnvelopeTimer = 0;
        private int ch1LengthTimer = 0;
        private bool ch1LengthEnabled = false;
        private int sweepTimer = 0;
        private int shadowFreq = 0;
        private bool sweepEnabled = false;

        public byte NR21, NR22, NR23, NR24;
        public bool ch2IsPlaying = false;
        private int ch2CurrentVolume = 0;
        private int ch2EnvelopeTimer = 0;
        private int ch2LengthTimer = 0;
        private bool ch2LengthEnabled = false;

        public byte NR30, NR31, NR32, NR33, NR34;
        public byte[] WaveRam = new byte[16];
        public bool ch3IsPlaying = false;
        private int ch3LengthTimer = 0;
        private bool ch3AccessedRAMThisCycle = false;

        public byte NR41, NR42, NR43, NR44;
        public bool ch4IsPlaying = false;
        private int ch4CurrentVolume = 0;
        private int ch4EnvelopeTimer = 0;
        private int ch4LengthTimer = 0;
        private bool ch4LengthEnabled = false;
        private int lfsr = 0x7FFF;
        private double ch4Time = 0;


        private bool sweepNegateCalculated = false; // THE NEW FLAG
        private int ch3CorruptionOffset = 0;                                             // --- CHANNEL 3 STATE ---
        private bool waveSyncHack = false;
        // --- NEW: Wave RAM Cycle Synchronization ---
        private ulong globalApuCycles = 0;
        private ulong lastRamReadCycle1 = 0;
        private int lastRamReadIndex1 = 0;
        private ulong lastRamReadCycle2 = 0;
        private int lastRamReadIndex2 = 0;
        private ulong lastCh3TriggerCycle = 0;
        // NEW: Real-Time Playhead Trackers
        private int ch3FreqTimer = 0;
        private int ch3WavePosition = 0;
        public APU()
        {
            // Upgraded to 2 Channels (Stereo) for NR51 panning support!
            var waveFormat = new WaveFormat(SAMPLE_RATE, 16, 2);
            waveProvider = new BufferedWaveProvider(waveFormat);
            waveProvider.BufferLength = SAMPLE_RATE * 2;
            waveProvider.DiscardOnBufferOverflow = true;

            waveOut = new WaveOutEvent();
            waveOut.Init(waveProvider);
            waveOut.Play();
        }

        // ==========================================
        // ======= MEMORY ROUTING & POWER MGT =======
        // ==========================================

        // ==========================================
        // ======= PAN DOCS MEMORY ROUTING ==========
        // ==========================================

        // ==========================================
        // ======= PAN DOCS MEMORY ROUTING ==========
        // ==========================================

        public byte ReadRegister(ushort address)
        {
            if (address >= 0xFF30 && address <= 0xFF3F)
            {
                if (ch3IsPlaying && (NR30 & 0x80) != 0)
                {
                    // The CPU checks both, and if either matches a wave ram read, it reads that sample byte
                    if (globalApuCycles == lastRamReadCycle1) return WaveRam[lastRamReadIndex1];
                    if (globalApuCycles == lastRamReadCycle2) return WaveRam[lastRamReadIndex2];

                    // The Trigger Hack: Offset by 2 cycles
                    if (globalApuCycles == lastCh3TriggerCycle + 2) return WaveRam[address - 0xFF30];

                    return 0xFF; // Otherwise strictly blocked
                }
                return WaveRam[address - 0xFF30];
            }
            switch (address)
            {
                case 0xFF10: return (byte)(NR10 | 0x80);
                case 0xFF11: return (byte)(NR11 | 0x3F);
                case 0xFF12: return NR12;
                case 0xFF13: return 0xFF; // Write-Only
                case 0xFF14: return (byte)(NR14 | 0xBF);

                case 0xFF15: return 0xFF; // Unused 

                case 0xFF16: return (byte)(NR21 | 0x3F);
                case 0xFF17: return NR22;
                case 0xFF18: return 0xFF; // Write-Only
                case 0xFF19: return (byte)(NR24 | 0xBF);

                case 0xFF1A: return (byte)(NR30 | 0x7F);
                case 0xFF1B: return 0xFF; // Write-Only
                case 0xFF1C: return (byte)(NR32 | 0x9F);
                case 0xFF1D: return 0xFF; // Write-Only
                case 0xFF1E: return (byte)(NR34 | 0xBF);

                case 0xFF1F: return 0xFF; // Unused

                case 0xFF20: return 0xFF; // Write-Only
                case 0xFF21: return NR42;
                case 0xFF22: return NR43;
                case 0xFF23: return (byte)(NR44 | 0xBF);

                case 0xFF24: return NR50;
                case 0xFF25: return NR51;
                case 0xFF26:
                    byte value = (byte)(NR52 & 0x80);
                    if (ch1IsPlaying) value |= 0x01;
                    if (ch2IsPlaying) value |= 0x02;
                    if (ch3IsPlaying) value |= 0x04;
                    if (ch4IsPlaying) value |= 0x08;
                    return (byte)(value | 0x70);

                default: return 0xFF;
            }
        }

        public void WriteRegister(ushort address, byte value)
        {
            // 1. Master Power Switch (Can ALWAYS be written to!)
            // 1. MASTER POWER HANDLING
            if (address == 0xFF26)
            {
                bool wasOn = (NR52 & 0x80) != 0;
                NR52 = value;
                bool nowOn = (NR52 & 0x80) != 0;

                if (!nowOn)
                {
                    PowerOff();
                }
                else if (!wasOn && nowOn)
                {
                    // THE FIX: Set to 7, so when it ticks 8192 cycles later, (7 + 1) % 8 = Step 0!
                    frameSequencerStep = 7;
                    apuCycles = 0;
                }
                return;
            }

            // 2. APU IS DEAD CHECK
            if ((NR52 & 0x80) == 0)
            {
                if (address == 0xFF11) { NR11 = (byte)(value & 0x3F); ch1LengthTimer = 64 - (value & 0x3F); }
                else if (address == 0xFF16) { NR21 = (byte)(value & 0x3F); ch2LengthTimer = 64 - (value & 0x3F); }
                else if (address == 0xFF1B) { NR31 = value; ch3LengthTimer = 256 - value; }
                else if (address == 0xFF20) { NR41 = (byte)(value & 0x3F); ch4LengthTimer = 64 - (value & 0x3F); }

                else if (address >= 0xFF30 && address <= 0xFF3F) WaveRam[address - 0xFF30] = value;

                return;
            }

            // 3. NORMAL HARDWARE WRITES (Only executes if APU is ON)
            switch (address)
            {
                // --- CHANNEL 1 ---
                case 0xFF10:
                    {
                        bool wasNegate = (NR10 & 0x08) != 0;
                        bool nowNegate = (value & 0x08) != 0;

                        // The Hardware Defect: Switching from Subtraction to Addition after a calculation kills the channel!
                        if (wasNegate && !nowNegate && sweepNegateCalculated)
                        {
                            ch1IsPlaying = false;
                        }

                        NR10 = value;
                        break;
                    }
                case 0xFF11:
                    NR11 = value;
                    ch1LengthTimer = 64 - (value & 0x3F); // Reload immediately!
                    break;
                case 0xFF12:
                    NR12 = value;
                    if ((NR12 & 0xF8) == 0) ch1IsPlaying = false;
                    break;
                case 0xFF13: NR13 = value; break;
                case 0xFF14:
                    {
                        bool wasEnabled = (NR14 & 0x40) != 0;
                        bool nowEnabled = (value & 0x40) != 0;
                        bool firstHalfOfPeriod = (frameSequencerStep % 2 == 0); // Steps 0, 2, 4, 6

                        // The Extra Length Clock Quirk!
                        if (!wasEnabled && nowEnabled && firstHalfOfPeriod)
                        {
                            if (ch1LengthTimer > 0 && --ch1LengthTimer == 0) ch1IsPlaying = false;
                        }

                        NR14 = value;

                        if ((value & 0x80) != 0)
                        {
                            // Only reload length if it is currently 0!
                            if (ch1LengthTimer == 0)
                            {
                                ch1LengthTimer = 64;
                                // If we just enabled it, the extra clock drops the new 64 down to 63!
                                if (nowEnabled && firstHalfOfPeriod) ch1LengthTimer--;
                            }
                            if ((NR12 & 0xF8) != 0) TriggerChannel1();
                        }
                        break;
                    }

                case 0xFF16:
                    NR21 = value;
                    ch2LengthTimer = 64 - (value & 0x3F); // Reload immediately!
                    break;
                case 0xFF17:
                    NR22 = value;
                    if ((NR22 & 0xF8) == 0) ch2IsPlaying = false;
                    break;
                case 0xFF18: NR23 = value; break;
                case 0xFF19:
                    {
                        bool wasEnabled = (NR24 & 0x40) != 0;
                        bool nowEnabled = (value & 0x40) != 0;
                        bool firstHalfOfPeriod = (frameSequencerStep % 2 == 0);

                        if (!wasEnabled && nowEnabled && firstHalfOfPeriod)
                        {
                            if (ch2LengthTimer > 0 && --ch2LengthTimer == 0) ch2IsPlaying = false;
                        }

                        NR24 = value;

                        if ((value & 0x80) != 0)
                        {
                            if (ch2LengthTimer == 0)
                            {
                                ch2LengthTimer = 64;
                                if (nowEnabled && firstHalfOfPeriod) ch2LengthTimer--;
                            }
                            if ((NR22 & 0xF8) != 0) TriggerChannel2();
                        }
                        break;
                    }

                // --- CHANNEL 3 ---
                case 0xFF1A:
                    NR30 = value;
                    if ((NR30 & 0x80) == 0) ch3IsPlaying = false;
                    break;
                case 0xFF1B:
                    NR31 = value;
                    ch3LengthTimer = 256 - value; // Channel 3 uses a full 256-bit timer!
                    break;
                case 0xFF1C: NR32 = value; break;
                case 0xFF1D: NR33 = value; break;
                // --- CHANNEL 3 TRIGGER ---
                // --- CHANNEL 3 TRIGGER ---
                // --- CHANNEL 3 TRIGGER ---
                case 0xFF1E:
                    {
                        bool wasEnabled = (NR34 & 0x40) != 0;
                        bool nowEnabled = (value & 0x40) != 0;
                        bool firstHalfOfPeriod = (frameSequencerStep % 2 == 0);

                        if (!wasEnabled && nowEnabled && firstHalfOfPeriod)
                        {
                            if (ch3LengthTimer > 0 && --ch3LengthTimer == 0) ch3IsPlaying = false;
                        }

                        NR34 = value;

                        if ((value & 0x80) != 0)
                        {
                            // DMG HARDWARE BUG: Wave RAM Corruption!
                            if (ch3IsPlaying && (NR30 & 0x80) != 0)
                            {
                                // THE FIX: Use the REAL playhead, allowing your CPU timing to dictate the hardware short!
                                int currentByte = ch3WavePosition / 2;

                                if (currentByte < 4)
                                {
                                    WaveRam[0] = WaveRam[currentByte];
                                }
                                else
                                {
                                    int bankStart = (currentByte / 4) * 4;
                                    WaveRam[0] = WaveRam[bankStart + 0];
                                    WaveRam[1] = WaveRam[bankStart + 1];
                                    WaveRam[2] = WaveRam[bankStart + 2];
                                    WaveRam[3] = WaveRam[bankStart + 3];
                                }
                            }

                            if (ch3LengthTimer == 0)
                            {
                                ch3LengthTimer = 256;
                                if (nowEnabled && firstHalfOfPeriod) ch3LengthTimer--;
                            }
                            if ((NR30 & 0x80) != 0) TriggerChannel3();
                        }
                        break;
                    }

                case 0xFF20:
                    NR41 = value;
                    ch4LengthTimer = 64 - (value & 0x3F); // Reload immediately!
                    break;
                case 0xFF21:
                    NR42 = value;
                    if ((NR42 & 0xF8) == 0) ch4IsPlaying = false;
                    break;
                case 0xFF22: NR43 = value; break;
                // --- CHANNEL 4 TRIGGER ---
                case 0xFF23:
                    {
                        bool wasEnabled = (NR44 & 0x40) != 0;
                        bool nowEnabled = (value & 0x40) != 0;
                        bool firstHalfOfPeriod = (frameSequencerStep % 2 == 0);

                        if (!wasEnabled && nowEnabled && firstHalfOfPeriod)
                        {
                            if (ch4LengthTimer > 0 && --ch4LengthTimer == 0) ch4IsPlaying = false;
                        }

                        NR44 = value;

                        if ((value & 0x80) != 0)
                        {
                            if (ch4LengthTimer == 0)
                            {
                                ch4LengthTimer = 64;
                                if (nowEnabled && firstHalfOfPeriod) ch4LengthTimer--;
                            }
                            if ((NR42 & 0xF8) != 0) TriggerChannel4();
                        }
                        break;
                    }

                case 0xFF24: NR50 = value; break;

                case 0xFF25: NR51 = value; break;
                default:
                    if (address >= 0xFF30 && address <= 0xFF3F)
                    {
                        if (ch3IsPlaying && (NR30 & 0x80) != 0)
                        {
                            // The CPU checks both, and if either matches a wave ram read, it writes to that sample byte
                            if (globalApuCycles == lastRamReadCycle1)
                            {
                                WaveRam[lastRamReadIndex1] = value;
                                return;
                            }
                            if (globalApuCycles == lastRamReadCycle2)
                            {
                                WaveRam[lastRamReadIndex2] = value;
                                return;
                            }

                            // The Trigger Hack: Offset by 2 cycles
                            if (globalApuCycles == lastCh3TriggerCycle + 2)
                            {
                                WaveRam[address - 0xFF30] = value;
                                return;
                            }

                            return; // Otherwise strictly blocked
                        }

                        // Normal write when the channel is off
                        WaveRam[address - 0xFF30] = value;
                    }
                    break;
            }
        }

        private void PowerOff()
        {
            NR10 = 0;
            NR12 = NR13 = NR14 = 0;
            NR22 = NR23 = NR24 = 0;
            NR30 = NR32 = NR33 = NR34 = 0;
            NR42 = NR43 = NR44 = 0;
            NR50 = NR51 = 0;

            // Zero out the Duty bits, but strictly preserve the Length bits!
            NR11 &= 0x3F;
            NR21 &= 0x3F;
            NR41 &= 0x3F;
            // NR31 is 100% length, so we don't touch it at all.

            ch1IsPlaying = ch2IsPlaying = ch3IsPlaying = ch4IsPlaying = false;
        }

        // ==========================================
        // ============ HARDWARE TRIGGERS ===========
        // ==========================================

        private void TriggerChannel1()
        {
            ch1IsPlaying = true;
            ch1CurrentVolume = (NR12 >> 4) & 0x0F;
            int pace = NR12 & 0x07;
            ch1EnvelopeTimer = pace > 0 ? pace : 8;



            shadowFreq = NR13 | ((NR14 & 0x07) << 8);
            int sweepPace = (NR10 >> 4) & 0x07;
            int sweepStep = NR10 & 0x07;

            sweepTimer = sweepPace > 0 ? sweepPace : 8;
            sweepEnabled = sweepPace > 0 || sweepStep > 0;
            sweepNegateCalculated = false; // RESET THE FLAG HERE
            if (sweepStep > 0 && CalculateSweepFreq() > 2047) ch1IsPlaying = false;
        }

        private void TriggerChannel2()
        {
            ch2IsPlaying = true;
            ch2CurrentVolume = (NR22 >> 4) & 0x0F;
            int pace = NR22 & 0x07;
            ch2EnvelopeTimer = pace > 0 ? pace : 8;

        }

        public void TriggerChannel3()
        {
            if ((NR30 & 0x80) == 0)
            {
                ch3IsPlaying = false;
                return;
            }
            ch3IsPlaying = true;

            ch3WavePosition = 0;
            int rawFreq = NR33 | ((NR34 & 0x07) << 8);
            ch3FreqTimer = (2048 - rawFreq) * 2;

            // --- THE TRIGGER HACK ---
            // Record the exact cycle to offset tests later
            lastCh3TriggerCycle = globalApuCycles;

            // Reset the delay pipeline!

            waveSyncHack = false;
        }
        private void TriggerChannel4()
        {
            ch4IsPlaying = true;
            ch4CurrentVolume = (NR42 >> 4) & 0x0F;
            int pace = NR42 & 0x07;
            ch4EnvelopeTimer = pace > 0 ? pace : 8;

            lfsr = 0x7FFF;
        }

        // ==========================================
        // ============== MASTER CLOCKS =============
        // ==========================================

        public void Tick(int cycles)
        {
            // The Insight: The APU updates in 2-cycle increments!
            for (int i = 0; i < cycles; i += 2)
            {
                globalApuCycles += 2;
                apuCycles += 2;

                ch3AccessedRAMThisCycle = false;

                // --- Channel 3 Real-Time Playhead ---
                if (ch3IsPlaying && (NR30 & 0x80) != 0)
                {
                    ch3FreqTimer -= 2; // Tick down by 2 instead of the whole block
                    if (ch3FreqTimer <= 0)
                    {
                        int rawFreq = NR33 | ((NR34 & 0x07) << 8);
                        ch3FreqTimer += (2048 - rawFreq) * 2;

                        ch3WavePosition = (ch3WavePosition + 1) % 32;

                        // Shift history: store the last two samples read and their exact cycle count
                        lastRamReadCycle2 = lastRamReadCycle1;
                        lastRamReadIndex2 = lastRamReadIndex1;

                        lastRamReadCycle1 = globalApuCycles;
                        lastRamReadIndex1 = ch3WavePosition / 2;

                        ch3AccessedRAMThisCycle = true;
                    }
                }

                if (apuCycles >= 8192)
                {
                    apuCycles -= 8192;
                    frameSequencerStep = (frameSequencerStep + 1) % 8;

                    if (frameSequencerStep % 2 == 0) StepLengthCounter();
                    if (frameSequencerStep == 2 || frameSequencerStep == 6) StepSweep();
                    if (frameSequencerStep == 7) StepVolumeEnvelope();
                }
            }
        }
        private void StepLengthCounter()
        {
            if (((NR14 & 0x40) != 0) && ch1LengthTimer > 0 && --ch1LengthTimer == 0) ch1IsPlaying = false;
            if (((NR24 & 0x40) != 0) && ch2LengthTimer > 0 && --ch2LengthTimer == 0) ch2IsPlaying = false;
            if (((NR34 & 0x40) != 0) && ch3LengthTimer > 0 && --ch3LengthTimer == 0) ch3IsPlaying = false;
            if (((NR44 & 0x40) != 0) && ch4LengthTimer > 0 && --ch4LengthTimer == 0) ch4IsPlaying = false;
        }

        private void StepSweep()
        {
            if (!sweepEnabled) return;

            sweepTimer--;
            if (sweepTimer <= 0)
            {
                int sweepPace = (NR10 >> 4) & 0x07;
                sweepTimer = sweepPace > 0 ? sweepPace : 8;

                if (sweepPace > 0)
                {
                    int newFreq = CalculateSweepFreq();
                    int sweepStep = NR10 & 0x07;

                    if (newFreq <= 2047 && sweepStep > 0)
                    {
                        shadowFreq = newFreq;
                        NR13 = (byte)(newFreq & 0xFF);
                        NR14 = (byte)((NR14 & 0xF8) | ((newFreq >> 8) & 0x07));

                        if (CalculateSweepFreq() > 2047) ch1IsPlaying = false;
                    }
                    else if (newFreq > 2047)
                    {
                        ch1IsPlaying = false;
                    }
                }
            }
        }

        private int CalculateSweepFreq()
        {
            int sweepStep = NR10 & 0x07;
            int freqOffset = shadowFreq >> sweepStep;

            // Bit 3 determines direction: 0 = Addition, 1 = Subtraction (Negate)
            if ((NR10 & 0x08) != 0)
            {
                sweepNegateCalculated = true; // WE CALCULATED IN SUBTRACTION MODE!
                return shadowFreq - freqOffset;
            }
            else
            {
                return shadowFreq + freqOffset;
            }
        }
        private void StepVolumeEnvelope()
        {
            int pace1 = NR12 & 0x07;
            if (pace1 > 0 && --ch1EnvelopeTimer <= 0)
            {
                ch1EnvelopeTimer = pace1;
                int newVol = ch1CurrentVolume + ((NR12 & 0x08) != 0 ? 1 : -1);
                if (newVol >= 0 && newVol <= 15) ch1CurrentVolume = newVol;
            }

            int pace2 = NR22 & 0x07;
            if (pace2 > 0 && --ch2EnvelopeTimer <= 0)
            {
                ch2EnvelopeTimer = pace2;
                int newVol = ch2CurrentVolume + ((NR22 & 0x08) != 0 ? 1 : -1);
                if (newVol >= 0 && newVol <= 15) ch2CurrentVolume = newVol;
            }

            int pace4 = NR42 & 0x07;
            if (pace4 > 0 && --ch4EnvelopeTimer <= 0)
            {
                ch4EnvelopeTimer = pace4;
                int newVol = ch4CurrentVolume + ((NR42 & 0x08) != 0 ? 1 : -1);
                if (newVol >= 0 && newVol <= 15) ch4CurrentVolume = newVol;
            }
        }

        // ==========================================
        // ============ AUDIO SYNTHESIS =============
        // ==========================================

        private double GetDutyThreshold(byte register)
        {
            int pattern = (register >> 6) & 0x03;
            if (pattern == 0) return 0.125;
            if (pattern == 1) return 0.250;
            if (pattern == 2) return 0.500;
            return 0.750;
        }

        private double GetNoiseFrequency()
        {
            int[] divisors = { 8, 16, 32, 48, 64, 80, 96, 112 };
            int clockShift = (NR43 >> 4) & 0x0F;
            int baseDivisor = divisors[NR43 & 0x07];
            return 4194304.0 / (baseDivisor << clockShift);
        }

        public void ProcessAudio()
        {
            if (waveProvider.BufferedBytes > SAMPLE_RATE) return;

            int samplesToGenerate = SAMPLE_RATE / 60;
            // 4 bytes per stereo sample (2 bytes Left, 2 bytes Right)
            byte[] buffer = new byte[samplesToGenerate * 4];
            double timePerSample = 1.0 / SAMPLE_RATE;

            for (int i = 0; i < samplesToGenerate; i++)
            {
                double ch1Sample = 0;
                double ch2Sample = 0;
                double ch3Sample = 0;
                double ch4Sample = 0;

                // 1. Synthesize Waveforms
                if (ch1IsPlaying && ch1CurrentVolume > 0)
                {
                    double freq = 131072.0 / (2048 - (NR13 | ((NR14 & 0x07) << 8)));
                    double amp = (ch1CurrentVolume / 15.0) * 8000.0;
                    ch1Sample = ((time * freq) % 1.0) < GetDutyThreshold(NR11) ? amp : -amp;
                }

                if (ch2IsPlaying && ch2CurrentVolume > 0)
                {
                    double freq = 131072.0 / (2048 - (NR23 | ((NR24 & 0x07) << 8)));
                    double amp = (ch2CurrentVolume / 15.0) * 8000.0;
                    ch2Sample = ((time * freq) % 1.0) < GetDutyThreshold(NR21) ? amp : -amp;
                }

                if (ch3IsPlaying && (NR30 & 0x80) != 0)
                {
                    double freq = 65536.0 / (2048 - (NR33 | ((NR34 & 0x07) << 8)));
                    int sampleIndex = Math.Min(31, (int)(((time * freq) % 1.0) * 32.0));
                    int nibble = (sampleIndex % 2 == 0) ? (WaveRam[sampleIndex / 2] >> 4) : (WaveRam[sampleIndex / 2] & 0x0F);

                    int volCode = (NR32 >> 5) & 0x03;
                    double ampMulti = volCode == 1 ? 1.0 : (volCode == 2 ? 0.5 : (volCode == 3 ? 0.25 : 0.0));
                    ch3Sample = ((nibble - 7.5) / 7.5) * ampMulti * 8000.0;
                }

                if (ch4IsPlaying && ch4CurrentVolume > 0)
                {
                    double freq = GetNoiseFrequency();
                    ch4Time += timePerSample;
                    if (ch4Time >= (1.0 / freq))
                    {
                        ch4Time -= (1.0 / freq);
                        int xor = (lfsr & 1) ^ ((lfsr >> 1) & 1);
                        lfsr = (lfsr >> 1) | (xor << 14);
                        if ((NR43 & 0x08) != 0) lfsr = (lfsr & ~0x40) | (xor << 6);
                    }
                    double amp = (ch4CurrentVolume / 15.0) * 8000.0;
                    ch4Sample = ((lfsr & 1) == 0) ? amp : -amp;
                }

                // 2. Mix Stereo Output based on NR51 Panning
                double leftMix = 0;
                double rightMix = 0;

                if ((NR51 & 0x10) != 0) leftMix += ch1Sample;
                if ((NR51 & 0x20) != 0) leftMix += ch2Sample;
                if ((NR51 & 0x40) != 0) leftMix += ch3Sample;
                if ((NR51 & 0x80) != 0) leftMix += ch4Sample;

                if ((NR51 & 0x01) != 0) rightMix += ch1Sample;
                if ((NR51 & 0x02) != 0) rightMix += ch2Sample;
                if ((NR51 & 0x04) != 0) rightMix += ch3Sample;
                if ((NR51 & 0x08) != 0) rightMix += ch4Sample;

                // 3. Apply Master Volume (NR50)
                // Hardware volume scaling is: (Volume Register + 1) / 8
                int leftVol = (NR50 >> 4) & 0x07;
                int rightVol = NR50 & 0x07;

                leftMix = (leftMix / 4.0) * ((leftVol + 1) / 8.0);
                rightMix = (rightMix / 4.0) * ((rightVol + 1) / 8.0);

                // 4. Convert and Write
                short finalLeft = (short)leftMix;
                short finalRight = (short)rightMix;

                buffer[i * 4] = (byte)(finalLeft & 0xFF);
                buffer[i * 4 + 1] = (byte)((finalLeft >> 8) & 0xFF);
                buffer[i * 4 + 2] = (byte)(finalRight & 0xFF);
                buffer[i * 4 + 3] = (byte)((finalRight >> 8) & 0xFF);

                time += timePerSample;
            }

            waveProvider.AddSamples(buffer, 0, buffer.Length);
        }
    }
}
