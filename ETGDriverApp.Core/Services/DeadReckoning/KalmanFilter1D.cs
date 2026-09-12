namespace ETGDriverApp.Core.Services.DeadReckoning;

internal class KalmanFilter1D(double initialEstimate, double initialErrorEstimate = 1, double errorMeasure = 1, double processNoise = 0.01)
{
    private double _estimate = initialEstimate;
    private double _errorEstimate = initialErrorEstimate;
    private readonly double _errorMeasure = errorMeasure;
    private readonly double _q = processNoise;

    public double Update(double measurement)
    {
        _errorEstimate += _q;
        var kalmanGain = _errorEstimate / (_errorEstimate + _errorMeasure);
        _estimate += kalmanGain * (measurement - _estimate);
        _errorEstimate *= (1 - kalmanGain);
        return _estimate;
    }
}
