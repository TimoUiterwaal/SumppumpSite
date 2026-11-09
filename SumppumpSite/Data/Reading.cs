using Microsoft.EntityFrameworkCore;

namespace SumppumpSite.Data;

// One row per post from the ESP32 (every 15 s).
[Index(nameof(Ts))]
public class Reading
{
    public long Id { get; set; }
    public long Ts { get; set; }        // Unix time in milliseconds, UTC
    public double Level { get; set; }   // inches, as calibrated on the ESP32
    public double Volts { get; set; }   // raw probe voltage, kept so old data can be recalibrated
}
