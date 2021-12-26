﻿using System;

namespace Wabbajack.Lib.Interventions
{
    public interface IStatusMessage
    {
        DateTime Timestamp { get; }
        string ShortDescription { get; }
        string ExtendedDescription { get; }
    }
}
