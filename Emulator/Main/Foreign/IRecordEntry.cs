//
// Copyright (c) Antmicro
// Copyright (c) Realtime Embedded
//
// This file is part of the Emul8 project.
// Full license details are defined in the 'LICENSE' file.
//
using System;

namespace Emul8.EventRecording
{
    public interface IRecordEntry
    {
        void Play(Func<string, Delegate, Delegate> handlerResolver);
        long SyncNumber { get; }
    }
}
