// Based on siddefs.h from reSID, a MOS6581 SID emulator engine.
// Copyright (C) 2004 Dag Lem <resid@nimrod.no>
// Translated to C# and modified by Magnar Harestad, 2026.
//
// This file is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 2
// of the License, or (at your option) any later version. It is distributed WITHOUT ANY
// WARRANTY; see the LICENSE file for details.

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
