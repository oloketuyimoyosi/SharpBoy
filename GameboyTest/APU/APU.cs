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
        // --- NAUDIO AUDIO PIPELINE ---
        private WaveOutEvent waveOut;
        private BufferedWaveProvider waveProvider;
        private const int SAMPLE_RATE = 44100;
        private double time = 0;

        // --- CHANNEL 1 REGISTERS & STATE ---
        public byte NR10, NR11, NR12, NR13, NR14;
        public bool ch1IsPlaying = false;
        private int ch1CurrentVolume = 0;
        private int ch1EnvelopeTimer = 0;
        private int ch1LengthTimer = 0;
        private bool ch1LengthEnabled = false;

        // --- CHANNEL 1 SWEEP STATE ---
        private int sweepTimer = 0;
        private int shadowFreq = 0;
        private bool sweepEnabled = false;

        // --- CHANNEL 2 REGISTERS & STATE ---
        public byte NR21, NR22, NR23, NR24;
        public bool ch2IsPlaying = false;
        private int ch2CurrentVolume = 0;
        private int ch2EnvelopeTimer = 0;
        private int ch2LengthTimer = 0;
        private bool ch2LengthEnabled = false;

        // --- MASTER CLOCKS ---
        private int apuCycles = 0;
        private int frameSequencerStep = 0;

        // --- CHANNEL 4 REGISTERS & STATE (NOISE) ---
        public byte NR41, NR42, NR43, NR44;
        public bool ch4IsPlaying = false;
        private int ch4CurrentVolume = 0;
        private int ch4EnvelopeTimer = 0;
        private int ch4LengthTimer = 0;
        private bool ch4LengthEnabled = false;

        // --- CHANNEL 3 REGISTERS & STATE (WAVE RAM) ---
        public byte NR30, NR31, NR32, NR33, NR34;
        public byte[] WaveRam = new byte[16];
        public bool ch3IsPlaying = false;
        private int ch3LengthTimer = 0;
        private bool ch3LengthEnabled = false;

        // The Hardware Shift Register (Initialized to all 1s)
        private int lfsr = 0x7FFF;
        private double ch4Time = 0; // Separate time tracker for the noise phase
        public APU()
        {
            var waveFormat = new WaveFormat(SAMPLE_RATE, 16, 1);
            waveProvider = new BufferedWaveProvider(waveFormat);
            waveProvider.BufferLength = SAMPLE_RATE * 2;
            waveProvider.DiscardOnBufferOverflow = true;

            waveOut = new WaveOutEvent();
            waveOut.Init(waveProvider);
            waveOut.Play();
        }

        // ==========================================
        // ============ HARDWARE TRIGGERS ===========
        // ==========================================

        public void TriggerChannel1()
        {
            ch1IsPlaying = true;

            // 1. Initialize Envelope
            ch1CurrentVolume = (NR12 >> 4) & 0x0F;
            int pace = NR12 & 0x07;
            ch1EnvelopeTimer = pace > 0 ? pace : 8;

            // 2. Initialize Length
            int initialLength = NR11 & 0x3F;
            ch1LengthTimer = 64 - initialLength;
            if (ch1LengthTimer == 0) ch1LengthTimer = 64;

            // 3. Initialize Sweep
            shadowFreq = NR13 | ((NR14 & 0x07) << 8);
            int sweepPace = (NR10 >> 4) & 0x07;
            int sweepStep = NR10 & 0x07;

            sweepTimer = sweepPace > 0 ? sweepPace : 8;
            sweepEnabled = sweepPace > 0 || sweepStep > 0;

            if (sweepStep > 0 && CalculateSweepFreq() > 2047)
            {
                ch1IsPlaying = false;
            }
        }

        public void TriggerChannel2()
        {
            ch2IsPlaying = true;

            // 1. Initialize Envelope
            ch2CurrentVolume = (NR22 >> 4) & 0x0F;
            int pace = NR22 & 0x07;
            ch2EnvelopeTimer = pace > 0 ? pace : 8;

            // 2. Initialize Length
            int initialLength = NR21 & 0x3F;
            ch2LengthTimer = 64 - initialLength;
            if (ch2LengthTimer == 0) ch2LengthTimer = 64;
        }
        public void TriggerChannel4()
        {
            ch4IsPlaying = true;

            // 1. Initialize Envelope
            ch4CurrentVolume = (NR42 >> 4) & 0x0F;
            int pace = NR42 & 0x07;
            ch4EnvelopeTimer = pace > 0 ? pace : 8;

            // 2. Initialize Length (Uses the bottom 6 bits of NR41)
            int initialLength = NR41 & 0x3F;
            ch4LengthTimer = 64 - initialLength;
            if (ch4LengthTimer == 0) ch4LengthTimer = 64;

            // 3. Reset the Hardware Shift Register
            lfsr = 0x7FFF;
        }
        public void TriggerChannel3()
        {
            // The DAC Power switch (Bit 7 of NR30) must be ON to play sound
            if ((NR30 & 0x80) == 0)
            {
                ch3IsPlaying = false;
                return;
            }

            ch3IsPlaying = true;

            // Initialize Length (Channel 3 uses an 8-bit length timer instead of 6-bit!)
            int initialLength = NR31;
            ch3LengthTimer = 256 - initialLength;
            if (ch3LengthTimer == 0) ch3LengthTimer = 256;
        }

        // ==========================================
        // ============== MASTER CLOCKS =============
        // ==========================================

        public void Tick(int cycles)
        {
            apuCycles += cycles;

            // 512 Hz Frame Sequencer
            if (apuCycles >= 8192)
            {
                apuCycles -= 8192;
                frameSequencerStep = (frameSequencerStep + 1) % 8;

                // Length Counter (256 Hz) - Steps 0, 2, 4, 6
                if (frameSequencerStep % 2 == 0) StepLengthCounter();

                // Pitch Sweep (128 Hz) - Steps 2, 6
                if (frameSequencerStep == 2 || frameSequencerStep == 6) StepSweep();

                // Volume Envelope (64 Hz) - Step 7
                if (frameSequencerStep == 7) StepVolumeEnvelope();
            }
        }

        private void StepLengthCounter()
        {
            // Channel 1
            ch1LengthEnabled = (NR14 & 0x40) != 0;
            if (ch1LengthEnabled && ch1LengthTimer > 0)
            {
                ch1LengthTimer--;
                if (ch1LengthTimer == 0) ch1IsPlaying = false;
            }

            // Channel 2
            ch2LengthEnabled = (NR24 & 0x40) != 0;
            if (ch2LengthEnabled && ch2LengthTimer > 0)
            {
                ch2LengthTimer--;
                if (ch2LengthTimer == 0) ch2IsPlaying = false;
            }
            //Channel 3
            ch3LengthEnabled = (NR34 & 0x40) != 0;
            if (ch3LengthEnabled && ch3LengthTimer > 0)
            {
                ch3LengthTimer--;
                if (ch3LengthTimer == 0) ch3IsPlaying = false;
            }
            //Channel 4
            ch4LengthEnabled = (NR44 & 0x40) != 0;
            if (ch4LengthEnabled && ch4LengthTimer > 0)
            {
                ch4LengthTimer--;
                if (ch4LengthTimer == 0) ch4IsPlaying = false;
            }
        }
        private double GetNoiseFrequency()
        {
            // The Game Boy uses a lookup table for the base divisor
            int[] divisors = { 8, 16, 32, 48, 64, 80, 96, 112 };
            int clockShift = (NR43 >> 4) & 0x0F;
            int baseDivisor = divisors[NR43 & 0x07];

            // Frequency = Master Clock / (Divisor * 2^ClockShift)
            return 4194304.0 / (baseDivisor << clockShift);
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

            if ((NR10 & 0x08) != 0) return shadowFreq - freqOffset;
            else return shadowFreq + freqOffset;
        }

        private void StepVolumeEnvelope()
        {
            // Channel 1
            int pace1 = NR12 & 0x07;
            if (pace1 > 0)
            {
                ch1EnvelopeTimer--;
                if (ch1EnvelopeTimer <= 0)
                {
                    ch1EnvelopeTimer = pace1;
                    int direction = (NR12 & 0x08) != 0 ? 1 : -1;
                    int newVolume = ch1CurrentVolume + direction;
                    if (newVolume >= 0 && newVolume <= 15) ch1CurrentVolume = newVolume;
                }
            }

            // Channel 2
            int pace2 = NR22 & 0x07;
            if (pace2 > 0)
            {
                ch2EnvelopeTimer--;
                if (ch2EnvelopeTimer <= 0)
                {
                    ch2EnvelopeTimer = pace2;
                    int direction = (NR22 & 0x08) != 0 ? 1 : -1;
                    int newVolume = ch2CurrentVolume + direction;
                    if (newVolume >= 0 && newVolume <= 15) ch2CurrentVolume = newVolume;
                }
            }

            int pace4 = NR42 & 0x07;
            if (pace4 > 0)
            {
                ch4EnvelopeTimer--;
                if (ch4EnvelopeTimer <= 0)
                {
                    ch4EnvelopeTimer = pace4;
                    int direction = (NR42 & 0x08) != 0 ? 1 : -1;
                    int newVolume = ch4CurrentVolume + direction;
                    if (newVolume >= 0 && newVolume <= 15) ch4CurrentVolume = newVolume;
                }
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

        public void ProcessAudio()
        {
            if (waveProvider.BufferedBytes > SAMPLE_RATE) return;

            int samplesToGenerate = SAMPLE_RATE / 60;
            byte[] buffer = new byte[samplesToGenerate * 2];

            for (int i = 0; i < samplesToGenerate; i++)
            {
                short ch1Sample = 0;
                short ch2Sample = 0;
                short ch3Sample = 0; // NEW!
                // Inside your for loop in ProcessAudio():
                short ch4Sample = 0; // NEW!

                // ... (Your existing Ch1 and Ch2 generation code goes here) ...


                // 1. Synthesize Channel 1
                if (ch1IsPlaying && ch1CurrentVolume > 0)
                {
                    int rawFreq = NR13 | ((NR14 & 0x07) << 8);
                    double freq = 131072.0 / (2048 - rawFreq);
                    double phase = (time * freq) % 1.0;
                    double amp = (ch1CurrentVolume / 15.0) * 4000.0;

                    ch1Sample = (short)(phase < GetDutyThreshold(NR11) ? amp : -amp);
                }

                // 2. Synthesize Channel 2
                if (ch2IsPlaying && ch2CurrentVolume > 0)
                {
                    int rawFreq = NR23 | ((NR24 & 0x07) << 8);
                    double freq = 131072.0 / (2048 - rawFreq);
                    double phase = (time * freq) % 1.0;
                    double amp = (ch2CurrentVolume / 15.0) * 4000.0;

                    ch2Sample = (short)(phase < GetDutyThreshold(NR21) ? amp : -amp);
                }

                // 3. Mix and Output
                if (ch3IsPlaying && (NR30 & 0x80) != 0)
                {
                    int rawFreq = NR33 | ((NR34 & 0x07) << 8);

                    // Channel 3 processes 32 samples per cycle, so the base frequency is different!
                    double freq = 65536.0 / (2048 - rawFreq);
                    double phase = (time * freq) % 1.0;

                    // Map the 0.0 -> 1.0 phase to an index from 0 to 31
                    int sampleIndex = (int)(phase * 32.0);
                    if (sampleIndex > 31) sampleIndex = 31; // Safety check

                    // Each byte in Wave RAM holds TWO 4-bit samples. 
                    byte ramByte = WaveRam[sampleIndex / 2];

                    // Even indexes use the upper 4 bits, odd indexes use the lower 4 bits
                    int nibble = (sampleIndex % 2 == 0) ? (ramByte >> 4) : (ramByte & 0x0F);

                    // Channel 3 Volume Shift (Bits 5-6 of NR32)
                    // 00 = Mute, 01 = 100%, 10 = 50%, 11 = 25%
                    int volumeCode = (NR32 >> 5) & 0x03;
                    double ampMultiplier = 0.0;
                    if (volumeCode == 1) ampMultiplier = 1.0;
                    else if (volumeCode == 2) ampMultiplier = 0.5;
                    else if (volumeCode == 3) ampMultiplier = 0.25;

                    // Convert the 4-bit value (0 to 15) to an audio wave (-7.5 to +7.5) to center it
                    double centeredSample = (nibble - 7.5) / 7.5;

                    ch3Sample = (short)(centeredSample * ampMultiplier * 4000.0);
                }
                // 3. Synthesize Channel 4 (Noise)
                if (ch4IsPlaying && ch4CurrentVolume > 0)
                {
                    double freq = GetNoiseFrequency();
                    double timePerSample = 1.0 / SAMPLE_RATE;

                    ch4Time += timePerSample;

                    // Has enough time passed to shift the LFSR?
                    if (ch4Time >= (1.0 / freq))
                    {
                        ch4Time -= (1.0 / freq);

                        // LFSR Hardware Logic: XOR the bottom two bits
                        int xorResult = (lfsr & 1) ^ ((lfsr >> 1) & 1);
                        lfsr >>= 1; // Shift right
                        lfsr |= (xorResult << 14); // Put the result in Bit 14

                        // If Bit 3 of NR43 is set, it becomes a 7-bit LFSR (creates metallic/robotic sounds)
                        if ((NR43 & 0x08) != 0)
                        {
                            lfsr &= ~0x40; // Clear Bit 6
                            lfsr |= (xorResult << 6); // Put the result in Bit 6
                        }
                    }

                    // The output volume is based on the inverted 0th bit of the LFSR
                    double amp4 = (ch4CurrentVolume / 15.0) * 4000.0;
                    ch4Sample = (short)(((lfsr & 1) == 0) ? amp4 : -amp4);
                }

                // Mix all 4 channels together!
                short mixedSample = (short)((ch1Sample + ch2Sample + ch3Sample + ch4Sample) / 2.0);
                buffer[i * 2] = (byte)(mixedSample & 0xFF);
                buffer[i * 2 + 1] = (byte)((mixedSample >> 8) & 0xFF);

                time += 1.0 / SAMPLE_RATE;
            }

            waveProvider.AddSamples(buffer, 0, buffer.Length);
        }
    }
}
