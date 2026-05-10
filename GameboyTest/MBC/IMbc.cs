namespace GameboyTest.MBC
{
    public interface IMbc
    {
        byte Read(ushort address);
        void Write(ushort address, byte value);
    }
}