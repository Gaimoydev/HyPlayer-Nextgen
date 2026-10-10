using System;

namespace HyPlayer.LyricRenderer.Animator.EaseFunctions;

public class CustomElasticEase : EaseFunctionBase
{
    public float Springiness = 6;
    public float Oscillations = 1;

    protected override double EaseInCore(double normalizedTime)
    {
        double expo;
        expo = (Math.Exp(Springiness * normalizedTime) - 1.0) / (Math.Exp(Springiness) - 1.0);
        var t2 = normalizedTime * normalizedTime;
        var t4 = t2 * t2;
        var damping = 0.7 + 0.3 * t4 * t4;
        return damping * expo * Math.Sin((Math.PI * 2.0 * Oscillations + Math.PI * 0.5) * normalizedTime);
    }
}