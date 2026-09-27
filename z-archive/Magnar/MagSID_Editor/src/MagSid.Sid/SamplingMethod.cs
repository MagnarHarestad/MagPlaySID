namespace MagSid.Sid;

/// <summary>
/// Subset of reSID's siddefs.h sampling_method enum implemented here - the two
/// resample/FIR modes (SAMPLE_RESAMPLE_INTERPOLATE / SAMPLE_RESAMPLE_FAST) are out of
/// scope for this milestone (see the plan's "Utanför scope" section).
/// </summary>
public enum SamplingMethod
{
    Fast,
    Interpolate,
}
