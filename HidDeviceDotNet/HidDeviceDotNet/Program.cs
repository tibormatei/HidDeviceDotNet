using HidDeviceDotNet;

internal class Program
{
    private static void Main(string[] args)
    {
        ushort vid = 0x0F3F;
        ushort pid = 0x0100;
        HidDevice d = new HidDevice();

        d.OpenDevice(vid, pid);

        // Reading the device
        int read_step = 0;
        while (read_step < 20)
        {
            byte[] date = d.ReadDevice();

            for (int i = 0; i < date.Length; ++i)
            {
                Console.Write(date[i] + ", ");
            }
            Console.WriteLine();

            ++read_step;
        }

        // Writing the device
        byte[] send_data = new byte[21];
        const int USB_CMD_SET_LED = 8;
        const int LHP_LED_L2_GREEN = 10;

        send_data[1] = USB_CMD_SET_LED;
        send_data[4] = LHP_LED_L2_GREEN;
        send_data[5] = 0;

        d.WriteDevice(send_data);
    }
}
