using ChocolArm64;
using ChocolArm64.Events;
using ChocolArm64.Memory;
using Ryujinx.Core.Loaders;
using Ryujinx.Core.Loaders.Executables;
using Ryujinx.Core.OsHle.Exceptions;
using Ryujinx.Core.OsHle.Handles;
using Ryujinx.Core.OsHle.Svc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Core.OsHle
{
    public class Process : IDisposable
    {
        private const int  MaxStackSize  = 8 * 1024 * 1024;

        private const int  TlsSize       = 0x200;
        private const int  TotalTlsSlots = 32;
        private const int  TlsTotalSize  = TotalTlsSlots * TlsSize;
        private const long TlsPageAddr   = (AMemoryMgr.AddrSize - TlsTotalSize) & ~AMemoryMgr.PageMask;

        private Switch Ns;

        private ATranslator Translator;

        public int ProcessId { get; private set; }

        public AMemory Memory { get; private set; }

        public KProcessScheduler Scheduler { get; private set; }

        private SvcHandler SvcHandler;

        private ConcurrentDictionary<int, AThread> TlsSlots;

        private ConcurrentDictionary<long, HThread> ThreadsByTpidr;

        private List<Executable> Executables;

        private HThread MainThread;

        private long ImageBase;

        public Process(Switch Ns, AMemoryAlloc Allocator, int ProcessId)
        {
            this.Ns        = Ns;
            this.ProcessId = ProcessId;

            Memory = new AMemory(Ns.Ram, Allocator);

            Scheduler = new KProcessScheduler();

            SvcHandler = new SvcHandler(Ns, this);

            TlsSlots = new ConcurrentDictionary<int, AThread>();

            ThreadsByTpidr = new ConcurrentDictionary<long, HThread>();

            Executables = new List<Executable>();

            ImageBase = 0x8000000;

            Memory.Manager.MapPhys(
                TlsPageAddr,
                TlsTotalSize,
                (int)MemoryType.ThreadLocal,
                AMemoryPerm.RW);
        }

        public void LoadProgram(IExecutable Program)
        {
            Logging.Info($"Image base at 0x{ImageBase:x16}.");

            Executable Executable = new Executable(Program, Memory, ImageBase);

            Executables.Add(Executable);

            ImageBase = AMemoryHelper.PageRoundUp(Executable.ImageEnd);
        }

        public void SetEmptyArgs()
        {
            ImageBase += AMemoryMgr.PageSize;
        }

        public void InitializeHeap()
        {
            Memory.Manager.SetHeapAddr(MemoryRegions.HeapRegionAddress);
        }

        public bool Run(bool UseHbAbi = false)
        {
            if (Executables.Count == 0)
            {
                return false;
            }

            long StackBot = TlsPageAddr - MaxStackSize;

            Memory.Manager.MapPhys(StackBot, MaxStackSize, (int)MemoryType.Normal, AMemoryPerm.RW);

            int Handle = MakeThread(Executables[0].ImageBase, TlsPageAddr, 0, 0, 0);

            if (Handle == -1)
            {
                return false;
            }

            MainThread = Ns.Os.Handles.GetData<HThread>(Handle);

            if (UseHbAbi)
            {
                long HbAbiDataPosition = (Executables[0].ImageEnd + 0xfff) & ~0xfff;

                Homebrew.WriteHbAbiData(Memory, HbAbiDataPosition, Handle);

                MainThread.Thread.ThreadState.X0 = (ulong)HbAbiDataPosition;
                MainThread.Thread.ThreadState.X1 = ulong.MaxValue;
            }

            Scheduler.StartThread(MainThread);

            return true;
        }

        public void StopAllThreads()
        {
            if (MainThread != null)
            {
                while (MainThread.Thread.IsAlive)
                {
                    MainThread.Thread.StopExecution();
                }
            }

            foreach (AThread Thread in TlsSlots.Values)
            {
                while (Thread.IsAlive)
                {
                    Thread.StopExecution();
                }
            }
        }

        public int MakeThread(
            long EntryPoint,
            long StackTop,
            long ArgsPtr,
            int  Priority,
            int  ProcessorId)
        {
            ThreadPriority ThreadPrio;

            if (Priority < 12)
            {
                ThreadPrio = ThreadPriority.Highest;
            }
            else if (Priority < 24)
            {
                ThreadPrio = ThreadPriority.AboveNormal;
            }
            else if (Priority < 36)
            {
                ThreadPrio = ThreadPriority.Normal;
            }
            else if (Priority < 48)
            {
                ThreadPrio = ThreadPriority.BelowNormal;
            }
            else
            {
                ThreadPrio = ThreadPriority.Lowest;
            }

            AThread Thread = new AThread(GetTranslator(), Memory, ThreadPrio, EntryPoint);

            HThread ThreadHnd = new HThread(Thread, ProcessorId, Priority);

            int Handle = Ns.Os.Handles.GenerateId(ThreadHnd);

            int TlsSlot = GetFreeTlsSlot(Thread);

            if (TlsSlot == -1 || Handle  == -1)
            {
                return -1;
            }

            Thread.ThreadState.Break     += BreakHandler;
            Thread.ThreadState.SvcCall   += SvcHandler.SvcCall;
            Thread.ThreadState.Undefined += UndefinedHandler;
            Thread.ThreadState.ProcessId  = ProcessId;
            Thread.ThreadState.ThreadId   = Ns.Os.IdGen.GenerateId();
            Thread.ThreadState.Tpidr      = TlsPageAddr + TlsSlot * TlsSize;
            Thread.ThreadState.X0         = (ulong)ArgsPtr;
            Thread.ThreadState.X1         = (ulong)Handle;
            Thread.ThreadState.X31        = (ulong)StackTop;

            Thread.WorkFinished += ThreadFinished;

            ThreadsByTpidr.TryAdd(Thread.ThreadState.Tpidr, ThreadHnd);

            return Handle;
        }

        private void BreakHandler(object sender, AInstExceptionEventArgs e)
        {
            throw new GuestBrokeExecutionException();
        }

        private void UndefinedHandler(object sender, AInstUndefinedEventArgs e)
        {
            throw new UndefinedInstructionException(e.Position, e.RawOpCode);
        }

        private ATranslator GetTranslator()
        {
            if (Translator == null)
            {
                Dictionary<long, string> SymbolTable = new Dictionary<long, string>();

                foreach (Executable Exe in Executables)
                {
                    foreach (KeyValuePair<long, string> KV in Exe.SymbolTable)
                    {                        
                        SymbolTable.TryAdd(Exe.ImageBase + KV.Key, KV.Value);
                    }
                }

                Translator = new ATranslator(SymbolTable);

                Translator.CpuTrace += CpuTraceHandler;
            }

            return Translator;
        }

        private void CpuTraceHandler(object sender, ACpuTraceEventArgs e)
        {
            Logging.Trace($"Executing at 0x{e.Position:x16} {e.SubName}");
        }

        private int GetFreeTlsSlot(AThread Thread)
        {
            for (int Index = 1; Index < TotalTlsSlots; Index++)
            {
                if (TlsSlots.TryAdd(Index, Thread))
                {
                    return Index;
                }
            }

            return -1;
        }

        private void ThreadFinished(object sender, EventArgs e)
        {
            if (sender is AThread Thread)
            {
                TlsSlots.TryRemove(GetTlsSlot(Thread.ThreadState.Tpidr), out _);

                Ns.Os.IdGen.DeleteId(Thread.ThreadId);
            }
        }

        private int GetTlsSlot(long Position)
        {
            return (int)((Position - TlsPageAddr) / TlsSize);
        }

        public HThread GetThread(long Tpidr)
        {
            if (!ThreadsByTpidr.TryGetValue(Tpidr, out HThread Thread))
            {
                Logging.Error($"Thread with TPIDR 0x{Tpidr:x16} not found!");
            }

            return Thread;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool Disposing)
        {
            if (Disposing)
            {
                Scheduler.Dispose();
            }
        }
    }
}