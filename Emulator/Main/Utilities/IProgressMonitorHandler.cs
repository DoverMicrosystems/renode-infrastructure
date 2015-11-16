//
// Copyright (c) Antmicro
// Copyright (c) Realtime Embedded
//
// This file is part of the Emul8 project.
// Full license details are defined in the 'LICENSE' file.
//
using System;

namespace Emul8.Utilities
{
    public interface IProgressMonitorHandler
    {
        void Finish(int id);
        void Update(int id, string description, int? progress);
    }
}
