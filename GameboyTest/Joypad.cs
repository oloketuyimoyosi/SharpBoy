namespace GameboyTest
{
    public enum JoypadButton
    {
        Right, Left, Up, Down,
        A, B, Select, Start
    }

    public class Joypad
    {
        private MemoryBus bus;

        // Hardware state: 1 = Unpressed, 0 = Pressed
        private byte actionButtons = 0x0F;
        private byte directionButtons = 0x0F;

        // Tracks which buttons the game currently wants to read (Bits 4 & 5)
        private byte joypSelect = 0x30;

        public Joypad(MemoryBus bus)
        {
            this.bus = bus;
        }

        public byte ReadRegister()
        {
            byte result = 0x0F;

            // If Bit 5 is 0, the game wants Action buttons
            if ((joypSelect & 0x20) == 0) result &= actionButtons;

            // If Bit 4 is 0, the game wants Direction buttons
            if ((joypSelect & 0x10) == 0) result &= directionButtons;

            // Bits 6 & 7 are hardwired to 1. 
            return (byte)(0xC0 | joypSelect | result);
        }

        public void WriteRegister(byte value)
        {
            byte oldState = ReadRegister();

            // Only bits 4 and 5 are writable by the CPU.
            joypSelect = (byte)(value & 0x30);

            byte newState = ReadRegister();

            // If changing the selection caused a bit to drop from 1 to 0, fire the interrupt!
            if ((oldState & ~newState & 0x0F) != 0)
            {
                RequestInterrupt();
            }
        }

        public void PressButton(JoypadButton button)
        {
            byte mask = GetButtonMask(button);
            byte oldState = ReadRegister();

            if (IsAction(button)) actionButtons &= (byte)~mask;
            else directionButtons &= (byte)~mask;

            byte newState = ReadRegister();

            // If the button we just pressed is currently selected by the game, fire the interrupt!
            if ((oldState & ~newState & 0x0F) != 0)
            {
                RequestInterrupt();
            }
        }

        public void ReleaseButton(JoypadButton button)
        {
            byte mask = GetButtonMask(button);

            // Set back to 1 (Unpressed)
            if (IsAction(button)) actionButtons |= mask;
            else directionButtons |= mask;
        }

        private void RequestInterrupt()
        {
            // Fire the Joypad Interrupt (Bit 4 of IF register at 0xFF0F)
            byte currentIF = bus.ReadByte(0xFF0F);
            bus.WriteByte(0xFF0F, (byte)(currentIF | 0x10));
        }

        private bool IsAction(JoypadButton button)
        {
            return button == JoypadButton.A || button == JoypadButton.B ||
                   button == JoypadButton.Select || button == JoypadButton.Start;
        }

        private byte GetButtonMask(JoypadButton button)
        {
            switch (button)
            {
                case JoypadButton.Right:
                case JoypadButton.A:
                    return 0x01; // Bit 0
                case JoypadButton.Left:
                case JoypadButton.B:
                    return 0x02; // Bit 1
                case JoypadButton.Up:
                case JoypadButton.Select:
                    return 0x04; // Bit 2
                case JoypadButton.Down:
                case JoypadButton.Start:
                    return 0x08; // Bit 3
                default: return 0x00;
            }
        }
    }
}