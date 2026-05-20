using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GameboyTest.NewFolder
{
    public class APU
    {
        private WaveOutEvent waveOut;
        public BufferedWaveProvider waveProvider;
        private const int SAMPLE_RATE = 44100;

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
        private int sweepTimer = 0;
        private int shadowFreq = 0;
        private bool sweepEnabled = false;

        public byte NR21, NR22, NR23, NR24;
        public bool ch2IsPlaying = false;
        private int ch2CurrentVolume = 0;
        private int ch2EnvelopeTimer = 0;
        private int ch2LengthTimer = 0;

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
        private int lfsr = 0x7FFF;

        private bool sweepNegateCalculated = false;
        private bool waveSyncHack = false;

        // --- Wave RAM Cycle Synchronization (The Test 09/12 Hack) ---
        private ulong globalApuCycles = 0;
        private ulong lastRamReadCycle1 = 0;
        private int lastRamReadIndex1 = 0;
        private ulong lastRamReadCycle2 = 0;
        private int lastRamReadIndex2 = 0;
        private ulong lastCh3TriggerCycle = 0;

        // --- Real-Time Hardware Playheads ---
        private static readonly int[,] DutyTable = new int[4, 8] {
            { 0, 0, 0, 0, 0, 0, 0, 1 }, // 12.5%
            { 0, 0, 0, 0, 0, 0, 1, 1 }, // 25%
            { 0, 0, 0, 0, 1, 1, 1, 1 }, // 50%
            { 1, 1, 1, 1, 1, 1, 0, 0 }  // 75%
        };

        private int ch1FreqTimer = 0;
        private int ch1WavePosition = 0;

        private int ch2FreqTimer = 0;
        private int ch2WavePosition = 0;

        private int ch3FreqTimer = 0;
        private int ch3WavePosition = 0;

        private int ch4FreqTimer = 0;

        // --- Audio Downsampling Pipeline ---
        private int downsampleCounter = 0;
        private List<byte> sampleBuffer = new List<byte>(4096);
        private double hpfLeft = 0;
        private double hpfRight = 0;
        private double lastLeftMix = 0;
        private double lastRightMix = 0;
        public APU()
        {
            var waveFormat = new WaveFormat(SAMPLE_RATE, 16, 2);
            waveProvider = new BufferedWaveProvider(waveFormat);
            // Give ourselves a massive 1-second maximum safety net
            waveProvider.BufferLength = SAMPLE_RATE * 4; 
            waveProvider.DiscardOnBufferOverflow = true;

            waveOut = new WaveOutEvent();
            

            
            waveOut.Init(waveProvider);
            waveOut.Play();
        }

        // ==========================================
        // ======= MEMORY ROUTING & POWER MGT =======
        // ==========================================

        public byte ReadRegister(ushort address)
        {
            if (address >= 0xFF30 && address <= 0xFF3F)
            {
                if (ch3IsPlaying && (NR30 & 0x80) != 0)
                {
                    if (globalApuCycles == lastRamReadCycle1) return WaveRam[lastRamReadIndex1];
                    if (globalApuCycles == lastRamReadCycle2) return WaveRam[lastRamReadIndex2];
                    if (globalApuCycles == lastCh3TriggerCycle + 2) return WaveRam[address - 0xFF30];
                    return 0xFF;
                }
                return WaveRam[address - 0xFF30];
            }
            switch (address)
            {
                case 0xFF10: return (byte)(NR10 | 0x80);
                case 0xFF11: return (byte)(NR11 | 0x3F);
                case 0xFF12: return NR12;
                case 0xFF13: return 0xFF;
                case 0xFF14: return (byte)(NR14 | 0xBF);

                case 0xFF15: return 0xFF;

                case 0xFF16: return (byte)(NR21 | 0x3F);
                case 0xFF17: return NR22;
                case 0xFF18: return 0xFF;
                case 0xFF19: return (byte)(NR24 | 0xBF);

                case 0xFF1A: return (byte)(NR30 | 0x7F);
                case 0xFF1B: return 0xFF;
                case 0xFF1C: return (byte)(NR32 | 0x9F);
                case 0xFF1D: return 0xFF;
                case 0xFF1E: return (byte)(NR34 | 0xBF);

                case 0xFF1F: return 0xFF;

                case 0xFF20: return 0xFF;
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
            if (address == 0xFF26)
            {
                bool wasOn = (NR52 & 0x80) != 0;
                NR52 = value;
                bool nowOn = (NR52 & 0x80) != 0;

                if (!nowOn) PowerOff();
                else if (!wasOn && nowOn)
                {
                    frameSequencerStep = 7;
                    apuCycles = 0;
                }
                return;
            }

            if ((NR52 & 0x80) == 0)
            {
                if (address == 0xFF11) { NR11 = (byte)(value & 0x3F); ch1LengthTimer = 64 - (value & 0x3F); }
                else if (address == 0xFF16) { NR21 = (byte)(value & 0x3F); ch2LengthTimer = 64 - (value & 0x3F); }
                else if (address == 0xFF1B) { NR31 = value; ch3LengthTimer = 256 - value; }
                else if (address == 0xFF20) { NR41 = (byte)(value & 0x3F); ch4LengthTimer = 64 - (value & 0x3F); }
                else if (address >= 0xFF30 && address <= 0xFF3F) WaveRam[address - 0xFF30] = value;
                return;
            }

            switch (address)
            {
                case 0xFF10:
                    {
                        bool wasNegate = (NR10 & 0x08) != 0;
                        bool nowNegate = (value & 0x08) != 0;
                        if (wasNegate && !nowNegate && sweepNegateCalculated) ch1IsPlaying = false;
                        NR10 = value;
                        break;
                    }
                case 0xFF11:
                    NR11 = value;
                    ch1LengthTimer = 64 - (value & 0x3F);
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
                        bool firstHalfOfPeriod = (frameSequencerStep % 2 == 0);

                        if (!wasEnabled && nowEnabled && firstHalfOfPeriod)
                        {
                            if (ch1LengthTimer > 0 && --ch1LengthTimer == 0) ch1IsPlaying = false;
                        }

                        NR14 = value;

                        if ((value & 0x80) != 0)
                        {
                            if (ch1LengthTimer == 0)
                            {
                                ch1LengthTimer = 64;
                                if (nowEnabled && firstHalfOfPeriod) ch1LengthTimer--;
                            }
                            if ((NR12 & 0xF8) != 0) TriggerChannel1();
                        }
                        break;
                    }

                case 0xFF16:
                    NR21 = value;
                    ch2LengthTimer = 64 - (value & 0x3F);
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

                case 0xFF1A:
                    NR30 = value;
                    if ((NR30 & 0x80) == 0) ch3IsPlaying = false;
                    break;
                case 0xFF1B:
                    NR31 = value;
                    ch3LengthTimer = 256 - value;
                    break;
                case 0xFF1C: NR32 = value; break;
                case 0xFF1D: NR33 = value; break;
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
                            if (ch3IsPlaying && (NR30 & 0x80) != 0)
                            {
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
                    ch4LengthTimer = 64 - (value & 0x3F);
                    break;
                case 0xFF21:
                    NR42 = value;
                    if ((NR42 & 0xF8) == 0) ch4IsPlaying = false;
                    break;
                case 0xFF22: NR43 = value; break;
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
                            if (globalApuCycles == lastRamReadCycle1) { WaveRam[lastRamReadIndex1] = value; return; }
                            if (globalApuCycles == lastRamReadCycle2) { WaveRam[lastRamReadIndex2] = value; return; }
                            if (globalApuCycles == lastCh3TriggerCycle + 2) { WaveRam[address - 0xFF30] = value; return; }
                            return;
                        }
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

            NR11 &= 0x3F;
            NR21 &= 0x3F;
            NR41 &= 0x3F;

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
            sweepNegateCalculated = false;

            if (sweepStep > 0 && CalculateSweepFreq() > 2047) ch1IsPlaying = false;

            ch1WavePosition = 0;
            ch1FreqTimer = (2048 - (NR13 | ((NR14 & 0x07) << 8))) * 4;
        }

        private void TriggerChannel2()
        {
            ch2IsPlaying = true;
            ch2CurrentVolume = (NR22 >> 4) & 0x0F;
            int pace = NR22 & 0x07;
            ch2EnvelopeTimer = pace > 0 ? pace : 8;

            ch2WavePosition = 0;
            ch2FreqTimer = (2048 - (NR23 | ((NR24 & 0x07) << 8))) * 4;
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

            lastCh3TriggerCycle = globalApuCycles;
            waveSyncHack = false;
        }

        private void TriggerChannel4()
        {
            ch4IsPlaying = true;
            ch4CurrentVolume = (NR42 >> 4) & 0x0F;
            int pace = NR42 & 0x07;
            ch4EnvelopeTimer = pace > 0 ? pace : 8;

            lfsr = 0x7FFF;
            ch4FreqTimer = 0;
        }

        // ==========================================
        // ============== MASTER CLOCKS =============
        // ==========================================

        public void Tick(int cycles)
        {
            for (int i = 0; i < cycles; i += 2)
            {
                globalApuCycles += 2;
                apuCycles += 2;

                if (ch1IsPlaying)
                {
                    ch1FreqTimer -= 2;
                    if (ch1FreqTimer <= 0)
                    {
                        int rawFreq = NR13 | ((NR14 & 0x07) << 8);
                        ch1FreqTimer += (2048 - rawFreq) * 4;
                        ch1WavePosition = (ch1WavePosition + 1) % 8;
                    }
                }

                if (ch2IsPlaying)
                {
                    ch2FreqTimer -= 2;
                    if (ch2FreqTimer <= 0)
                    {
                        int rawFreq = NR23 | ((NR24 & 0x07) << 8);
                        ch2FreqTimer += (2048 - rawFreq) * 4;
                        ch2WavePosition = (ch2WavePosition + 1) % 8;
                    }
                }

                ch3AccessedRAMThisCycle = false;

                if (ch3IsPlaying && (NR30 & 0x80) != 0)
                {
                    ch3FreqTimer -= 2;
                    if (ch3FreqTimer <= 0)
                    {
                        int rawFreq = NR33 | ((NR34 & 0x07) << 8);
                        ch3FreqTimer += (2048 - rawFreq) * 2;

                        ch3WavePosition = (ch3WavePosition + 1) % 32;

                        lastRamReadCycle2 = lastRamReadCycle1;
                        lastRamReadIndex2 = lastRamReadIndex1;

                        lastRamReadCycle1 = globalApuCycles;
                        lastRamReadIndex1 = ch3WavePosition / 2;

                        ch3AccessedRAMThisCycle = true;
                    }
                }

                if (ch4IsPlaying)
                {
                    ch4FreqTimer -= 2;
                    if (ch4FreqTimer <= 0)
                    {
                        int[] divisors = { 8, 16, 32, 48, 64, 80, 96, 112 };
                        int clockShift = (NR43 >> 4) & 0x0F;
                        int baseDivisor = divisors[NR43 & 0x07];

                        ch4FreqTimer += (baseDivisor << clockShift);

                        int xor = (lfsr & 1) ^ ((lfsr >> 1) & 1);
                        lfsr = (lfsr >> 1) | (xor << 14);
                        if ((NR43 & 0x08) != 0) lfsr = (lfsr & ~0x40) | (xor << 6);
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

                // --- AUDIO DOWNSAMPLING ---
                downsampleCounter += SAMPLE_RATE;
                if (downsampleCounter >= 2097152)
                {
                    downsampleCounter -= 2097152;
                    SampleCurrentState();
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

            if ((NR10 & 0x08) != 0)
            {
                sweepNegateCalculated = true;
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

        private void SampleCurrentState()
        {
            double ch1Sample = 0;
            double ch2Sample = 0;
            double ch3Sample = 0;
            double ch4Sample = 0;

            // 1. Read Current Hardware State
            if (ch1IsPlaying && ch1CurrentVolume > 0)
            {
                int pattern = (NR11 >> 6) & 0x03;
                int bit = DutyTable[pattern, ch1WavePosition];
                ch1Sample = (((bit * ch1CurrentVolume) / 7.5) - 1.0) * 8000.0;
            }

            if (ch2IsPlaying && ch2CurrentVolume > 0)
            {
                int pattern = (NR21 >> 6) & 0x03;
                int bit = DutyTable[pattern, ch2WavePosition];
                ch2Sample = (((bit * ch2CurrentVolume) / 7.5) - 1.0) * 8000.0;
            }

            if (ch3IsPlaying && (NR30 & 0x80) != 0)
            {
                int nibble = (ch3WavePosition % 2 == 0) ? (WaveRam[ch3WavePosition / 2] >> 4) : (WaveRam[ch3WavePosition / 2] & 0x0F);
                int volCode = (NR32 >> 5) & 0x03;
                int shiftedNibble = volCode == 0 ? 0 : (nibble >> (volCode - 1));
                ch3Sample = ((shiftedNibble / 7.5) - 1.0) * 8000.0;
            }

            if (ch4IsPlaying && ch4CurrentVolume > 0)
            {
                int bit = ((lfsr & 1) == 0) ? 1 : 0;
                ch4Sample = (((bit * ch4CurrentVolume) / 7.5) - 1.0) * 8000.0;
            }

            // 2. Mix Stereo Output based on NR51 Panning
            double leftMix = 0, rightMix = 0;

            if ((NR51 & 0x10) != 0) leftMix += ch1Sample;
            if ((NR51 & 0x20) != 0) leftMix += ch2Sample;
            if ((NR51 & 0x40) != 0) leftMix += ch3Sample;
            if ((NR51 & 0x80) != 0) leftMix += ch4Sample;

            if ((NR51 & 0x01) != 0) rightMix += ch1Sample;
            if ((NR51 & 0x02) != 0) rightMix += ch2Sample;
            if ((NR51 & 0x04) != 0) rightMix += ch3Sample;
            if ((NR51 & 0x08) != 0) rightMix += ch4Sample;

            // 3. Apply Master Volume (NR50)
            int leftVol = (NR50 >> 4) & 0x07;
            int rightVol = NR50 & 0x07;

            leftMix = (leftMix / 4.0) * ((leftVol + 1) / 8.0);
            rightMix = (rightMix / 4.0) * ((rightVol + 1) / 8.0);

            // --- HIGH-PASS FILTER (The Anti-Pop Capacitor) ---
            // This equation safely removes DC offsets so the wave doesn't violently snap to 0!
            hpfLeft = 0.996 * (hpfLeft + leftMix - lastLeftMix);
            hpfRight = 0.996 * (hpfRight + rightMix - lastRightMix);

            lastLeftMix = leftMix;
            lastRightMix = rightMix;

            // 4. Convert and Queue
            short finalLeft = (short)hpfLeft;
            short finalRight = (short)hpfRight;

            sampleBuffer.Add((byte)(finalLeft & 0xFF));
            sampleBuffer.Add((byte)((finalLeft >> 8) & 0xFF));
            sampleBuffer.Add((byte)(finalRight & 0xFF));
            sampleBuffer.Add((byte)((finalRight >> 8) & 0xFF));

            // 5. Push to NAudio when we hit 1/60th of a second
            if (sampleBuffer.Count >= (SAMPLE_RATE / 60) * 4)
            {
                if (waveProvider.BufferedBytes < waveProvider.BufferLength)
                {
                    waveProvider.AddSamples(sampleBuffer.ToArray(), 0, sampleBuffer.Count);
                }
                sampleBuffer.Clear();
            }
        }

        public void ProcessAudio()
        {
            // Left completely empty so CPU.cs can call it safely without doing any damage!
        }
    }
}