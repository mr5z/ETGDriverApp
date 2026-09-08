namespace ETGDriverApp.Services;

// single source of truth the location feed writes and the fake sensors read
public class SimulatedVehicleState
{
    public double SpeedMps { get; set; }

    public double HeadingDegrees { get; set; }

    // degrees/sec the vehicle is turning; zero on a straight leg
    public double HeadingRateDegPerSec { get; set; }

    public bool IsMoving => SpeedMps > 0.5;
}