using System;
using LibreHardwareMonitor.Hardware;

class P
{
    static int Main()
    {
        try
        {
            var c = new Computer { IsCpuEnabled = true };
            c.Open();
            Console.WriteLine("LHM opened. Scanning CPU temperature sensors...");
            foreach (var hw in c.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    hw.Update();
                    Console.WriteLine("CPU: " + hw.Name);
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType == SensorType.Temperature)
                        {
                            var v = s.Value.HasValue ? s.Value.Value.ToString("F1") : "null";
                            Console.WriteLine("  TEMP  " + s.Name + " = " + v + " C");
                        }
                    }
                }
            }
            c.Close();
            Console.WriteLine("SPIKE_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SPIKE_FAIL: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }
}
