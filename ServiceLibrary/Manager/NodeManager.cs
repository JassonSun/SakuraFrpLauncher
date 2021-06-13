﻿using System.Threading.Tasks;
using System.Collections.Generic;

using SakuraLibrary.Proto;
using SakuraLibrary.Helper;

namespace SakuraFrpService.Manager
{
    public class NodeManager : Dictionary<int, Node>, IAsyncManager
    {
        public const string Tag = "Service/NodeManager";

        public readonly SakuraService Main;
        public readonly AsyncManager AsyncManager;

        public NodeManager(SakuraService main)
        {
            Main = main;

            AsyncManager = new AsyncManager(Run);
        }

        public async Task UpdateNodes()
        {
            PushMessageBase msg = new PushMessageBase()
            {
                Type = PushMessageID.UpdateNodes,
                DataNodes = new NodeList()
            };
            var nodes = await Natfrp.Request<Natfrp.GetNodes>("get_nodes");
            lock (this)
            {
                Clear();
                foreach (var j in nodes.Data)
                {
                    this[j.Id] = new Node()
                    {
                        Id = j.Id,
                        Name = j.Name,
                        Host = j.Host ?? "",
                        AcceptNew = j.AcceptNew
                    };
                }
                msg.DataNodes.Nodes.Add(Values);
            }
            Main.LogManager.Log(LogManager.CATEGORY_SERVICE_INFO, Tag, "节点信息同步成功");
            Main.Communication.PushMessage(msg);
        }

        protected void Run()
        {
            int delayTicks = 0;
            while (!AsyncManager.StopEvent.WaitOne(50))
            {
                if (delayTicks-- <= 0)
                {
                    try
                    {
                        UpdateNodes().Wait();
                        delayTicks = 20 * 3600 * 6;
                    }
                    catch
                    {
                        delayTicks = 20 * 5;
                    }
                }
            }
        }

        #region IAsyncManager

        public bool Running => AsyncManager.Running;

        public void Start() => AsyncManager.Start();

        public void Stop(bool kill = false) => AsyncManager.Stop(kill);

        #endregion
    }
}
