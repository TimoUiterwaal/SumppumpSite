namespace SumppumpSite.Data;

// One point on the chart. For long spans it stands for a bucket of readings:
// V is their average, Lo and Hi the lowest and highest.
public record ChartPoint(long T, double V, double Lo, double Hi);

public record ChartData(int BucketSeconds, int PumpRuns, double? Lowest, double? Highest, List<ChartPoint> Points);

// A horizontal line across the chart, in the same units as the level.
public record ChartMark(double Y, string Label);
