// Based on siddefs.h from reSID, a MOS6581 SID emulator engine.
// Copyright (C) 2004 Dag Lem <resid@nimrod.no>
// Translated to C# and modified by Magnar Harestad, 2026.
//
// This file is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 2
// of the License, or (at your option) any later version. It is distributed WITHOUT ANY
// WARRANTY; see the LICENSE file for details.

namespace MagSid.Sid;

/// <summary>Matches reSID's siddefs.h chip_model enum.</summary>
public enum ChipModel
{
    Mos6581,
    Mos8580,
}
