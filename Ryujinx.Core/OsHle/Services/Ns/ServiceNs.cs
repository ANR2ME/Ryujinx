using Ryujinx.Core.OsHle.Ipc;
using System.Collections.Generic;

namespace Ryujinx.Core.OsHle.IpcServices.Ns
{
    class ServiceNs : IIpcService
    {
        private Dictionary<int, ServiceProcessRequest> m_Commands;

        public IReadOnlyDictionary<int, ServiceProcessRequest> Commands => m_Commands;

        public ServiceNs()
        {
            m_Commands = new Dictionary<int, ServiceProcessRequest>()
            {
                //{ 1, Function }
            };
        }
    }
}