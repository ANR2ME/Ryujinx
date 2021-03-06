using ChocolArm64.Memory;
using Ryujinx.Core.OsHle;
using Ryujinx.Graphics.Gal;
using Ryujinx.Graphics.Gpu;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Core
{
    public class Switch : IDisposable
    {
        public IntPtr Ram {get; private set; }

        internal NsGpu     Gpu { get; private set; }
        internal Horizon   Os  { get; private set; }
        internal VirtualFs VFs { get; private set; }
        internal Hid       Hid { get; private set; }

        public event EventHandler Finish;

        public Switch(IGalRenderer Renderer)
        {
            Ram = Marshal.AllocHGlobal((IntPtr)AMemoryMgr.RamSize);

            Gpu = new NsGpu(Renderer);
            Os  = new Horizon(this);
            VFs = new VirtualFs();
            Hid = new Hid(this);
        }

        public void FinalizeAllProcesses()
        {
            Os.FinalizeAllProcesses();
        }

        public void LoadCart(string ExeFsDir, string RomFsFile = null)
        {
            Os.LoadCart(ExeFsDir, RomFsFile);
        }

        public void LoadProgram(string FileName)
        {
            Os.LoadProgram(FileName);
        }

        public void SendControllerButtons(HidControllerID ControllerId,
                                          HidControllerLayouts Layout,
                                          HidControllerKeys Buttons,
                                          JoystickPosition LeftJoystick,
                                          JoystickPosition RightJoystick)
        {
            Hid.SendControllerButtons(ControllerId, Layout, Buttons, LeftJoystick, RightJoystick);
        }
        
        public void SendTouchScreenEntry(HidTouchScreenEntryTouch TouchPoint)
        {
            Hid.SendTouchPoint(TouchPoint);
        }

        internal virtual void OnFinish(EventArgs e)
        {
            Finish?.Invoke(this, e);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                VFs.Dispose();
            }

            Marshal.FreeHGlobal(Ram);
        }
    }
}