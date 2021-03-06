using ChocolArm64.State;
using Ryujinx.Core.OsHle.Handles;

namespace Ryujinx.Core.OsHle.Svc
{
    partial class SvcHandler
    {
        private void SvcArbitrateLock(AThreadState ThreadState)
        {
            int  OwnerThreadHandle      =  (int)ThreadState.X0;
            long MutexAddress           = (long)ThreadState.X1;
            int  RequestingThreadHandle =  (int)ThreadState.X2;

            HThread RequestingThread = Ns.Os.Handles.GetData<HThread>(RequestingThreadHandle);

            Mutex M = new Mutex(Process, MutexAddress, OwnerThreadHandle);

            M = Ns.Os.Mutexes.GetOrAdd(MutexAddress, M);

            M.WaitForLock(RequestingThread, RequestingThreadHandle);

            ThreadState.X0 = (int)SvcResult.Success;
        }

        private void SvcArbitrateUnlock(AThreadState ThreadState)
        {
            long MutexAddress = (long)ThreadState.X0;

            if (Ns.Os.Mutexes.TryGetValue(MutexAddress, out Mutex M))
            {
                M.Unlock();
            }

            ThreadState.X0 = (int)SvcResult.Success;
        }

        private void SvcWaitProcessWideKeyAtomic(AThreadState ThreadState)
        {
            long MutexAddress   = (long)ThreadState.X0;
            long CondVarAddress = (long)ThreadState.X1;
            int  ThreadHandle   =  (int)ThreadState.X2;
            long Timeout        = (long)ThreadState.X3;

            HThread Thread = Ns.Os.Handles.GetData<HThread>(ThreadHandle);

            Mutex M = new Mutex(Process, MutexAddress, ThreadHandle);

            M = Ns.Os.Mutexes.GetOrAdd(MutexAddress, M);

            M.GiveUpLock(ThreadHandle);

            CondVar Cv = new CondVar(Process, CondVarAddress, Timeout);

            Cv = Ns.Os.CondVars.GetOrAdd(CondVarAddress, Cv);

            Cv.WaitForSignal(Thread);

            M.WaitForLock(Thread, ThreadHandle);

            ThreadState.X0 = (int)SvcResult.Success;
        }

        private void SvcSignalProcessWideKey(AThreadState ThreadState)
        {
            long CondVarAddress = (long)ThreadState.X0;
            int  Count          =  (int)ThreadState.X1;

            HThread CurrThread = Process.GetThread(ThreadState.Tpidr);

            if (Ns.Os.CondVars.TryGetValue(CondVarAddress, out CondVar Cv))
            {
                Cv.SetSignal(CurrThread, Count);
            }

            ThreadState.X0 = (int)SvcResult.Success;
        }
    }
}