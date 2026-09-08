using System.Numerics;

namespace ETGDriverApp.Core.Services.DeadReckoning.Sensors;

// Shared between the accelerometer and gyroscope sources. Vehicle yaw appears
// on the device axis aligned with gravity, which depends on how the phone sits
// in its mount - so heading rate is taken along the gravity vector rather than
// the device Z axis. Assumes a fixed mount.
internal class DeviceOrientationReference
{
    private const float GravitySmoothing = 0.1f;

    private Vector3 _gravity = new(0, 0, -1);
    private bool _initialized;

    public void UpdateFromAcceleration(Vector3 accelerationG)
    {
        if (!_initialized)
        {
            _gravity = accelerationG;
            _initialized = true;

            return;
        }

        _gravity += GravitySmoothing * (accelerationG - _gravity);
    }

    // signed rotation rate about the gravity axis, in the input's units.
    // Sign convention is absorbed by RecalibrateAgainst's heading offset.
    public double YawRateAbout(Vector3 angularVelocity)
    {
        var gravity = _gravity;
        var magnitude = gravity.Length();

        if (magnitude < 0.1f)
            return angularVelocity.Z;

        return Vector3.Dot(angularVelocity, gravity / magnitude);
    }
}
