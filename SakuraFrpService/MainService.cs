﻿using System;
using System.Text;
using System.Linq;
using System.Threading;
using System.ServiceProcess;
using System.Threading.Tasks;

using SakuraLibrary;
using SakuraLibrary.Pipe;
using SakuraLibrary.Proto;
using UserStatus = SakuraLibrary.Proto.User.Types.Status;

using SakuraFrpService.Manager;
using Tunnel = SakuraFrpService.Data.Tunnel;

namespace SakuraFrpService
{
    public partial class MainService : ServiceBase
    {
        public User UserInfo = new User()
        {
            Status = UserStatus.NoLogin
        };

        public readonly bool Daemonize;
        public readonly Thread TickThread = null;

        public readonly PipeServer Pipe;
        public readonly LogManager LogManager;
        public readonly NodeManager NodeManager;
        public readonly TunnelManager TunnelManager;
        public readonly UpdateManager UpdateManager;

        public MainService(bool daemon)
        {
            var settings = Properties.Settings.Default;
            if (settings.UpgradeRequired)
            {
                settings.Upgrade();
                settings.UpgradeRequired = false;
                settings.Save();
            }
            Natfrp.BypassProxy = settings.BypassProxy;

            Daemonize = daemon;
            if (!daemon)
            {
                TickThread = new Thread(new ThreadStart(Tick));
            }

            InitializeComponent();

            Pipe = new PipeServer(Utils.InstallationPipeName);
            Pipe.DataReceived += Pipe_DataReceived;

            LogManager = new LogManager(this, 8192);
            NodeManager = new NodeManager(this);
            TunnelManager = new TunnelManager(this);
            UpdateManager = new UpdateManager(this);
        }

        public void Save()
        {
            var settings = Properties.Settings.Default;

            lock (UserInfo)
            {
                settings.Token = Natfrp.Token;
                settings.LoggedIn = UserInfo.Status == UserStatus.LoggedIn;
            }
            if (TunnelManager.Running)
            {
                settings.EnabledTunnels = TunnelManager.GetEnabledTunnels();
            }
            if (UpdateManager.Running)
            {
                settings.UpdateInterval = UpdateManager.UpdateInterval;
            }
            settings.BypassProxy = Natfrp.BypassProxy;

            settings.Save();
        }

        public void Tick()
        {
            ulong tick = 0;
            try
            {
                if (tick % 100 == 0)
                {
                    lock (UserInfo)
                    {
                        if (UserInfo.Status == UserStatus.NoLogin && Properties.Settings.Default.LoggedIn)
                        {
                            var _ = Login(Properties.Settings.Default.Token);
                        }
                    }
                }
                if (tick % 200 == 0)
                {
                    if (!Pipe.Running)
                    {
                        Pipe.Start();
                    }
                }
                tick++;
            }
            catch { }
            Thread.Sleep(50);
        }

        public void DaemonRun(string[] args)
        {
            if (!Daemonize)
            {
                throw new InvalidOperationException();
            }
            OnStart(args);
            while (true)
            {
                Tick();
            }
        }

        protected void PushUserInfo()
        {
            lock (UserInfo)
            {
                Pipe.PushMessage(new PushMessageBase()
                {
                    Type = PushMessageID.UpdateUser,
                    DataUser = UserInfo
                });
            }
        }

        protected async Task<string> Login(string token)
        {
            lock (UserInfo)
            {
                if (UserInfo.Status != UserStatus.NoLogin)
                {
                    return UserInfo.Status == UserStatus.Pending ? "操作进行中, 请稍候" : "用户已登录";
                }
                if (token.Length != 16)
                {
                    return "Token  无效";
                }
                UserInfo.Status = UserStatus.Pending;
                PushUserInfo();
            }
            LogManager.Log(1, "Service", "开始登录, Token: " + token.Substring(0, 4) + "********" + token.Substring(12));
            try
            {
                Natfrp.Token = token;

                var user = await Natfrp.Request<Natfrp.GetUser>("get_user");
                if (!user.Data.Login)
                {
                    return user.Message;
                }

                lock (UserInfo)
                {
                    UserInfo.Id = user.Data.Id;
                    UserInfo.Name = user.Data.Name;
                    UserInfo.Meta = user.Data.Meta;

                    UserInfo.Status = UserStatus.LoggedIn;
                }

                Save();

                NodeManager.Clear();
                NodeManager.Start();

                TunnelManager.Clear();
                TunnelManager.Start();

                PushUserInfo();
                LogManager.Log(1, "Service", "用户登录成功");
            }
            catch (Exception e)
            {
                LogManager.Log(3, "Service", "用户登录失败: " + e.ToString());
                Logout(true);
                return e.ToString();
            }
            return null;
        }

        protected string Logout(bool force = false)
        {
            lock (UserInfo)
            {
                if (!force && UserInfo.Status != UserStatus.LoggedIn)
                {
                    return UserInfo.Status == UserStatus.Pending ? "操作进行中, 请稍候" : null;
                }
                UserInfo.Status = UserStatus.Pending;
                PushUserInfo();
            }
            try
            {
                Properties.Settings.Default.Token = "";
                Properties.Settings.Default.LoggedIn = false;
                TunnelManager.Stop();
                NodeManager.Stop();
            }
            catch (Exception e)
            {
                return "未知错误:\n" + e.ToString();
            }
            finally
            {
                lock (UserInfo)
                {
                    UserInfo.Status = UserStatus.NoLogin;
                }
                Save();
                PushUserInfo();
            }
            LogManager.Log(1, "Service", "已退出登录");
            return null;
        }

        #region ServiceBase Override

        public new void Stop()
        {
            if (Daemonize)
            {
                OnStop();
                return;
            }
            base.Stop();
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                Pipe.Start();
                LogManager.Start();
                if (!Daemonize)
                {
                    TickThread.Start();
                }
                if (UpdateManager.LoadFrpcVersion())
                {
                    UpdateManager.Start();
                }
                else
                {
                    LogManager.Log(2, "Service", "UpdateManager: 无法获取 frpc 版本, 更新检查将不会启用");
                }
            }
            catch
            {
                ExitCode = 1;
                Stop();
                return;
            }
            LogManager.Log(1, "Service", "守护进程启动成功");
        }

        protected override void OnStop()
        {
            if (!Daemonize)
            {
                RequestAdditionalTime(60000);
            }
            try
            {
                Pipe.Stop();
                TunnelManager.Stop(true);
                NodeManager.Stop(true);
                if (TickThread != null)
                {
                    TickThread.Abort();
                }
                UpdateManager.Stop(true);
            }
            catch { }
            LogManager.Stop();
            if (Daemonize)
            {
                Environment.Exit(0);
            }
        }

        #endregion

        #region IPC Handling

        protected ResponseBase ResponseBase(bool success, string message = null) => new ResponseBase()
        {
            Success = success,
            Message = message ?? ""
        };

        private void Pipe_DataReceived(PipeConnection connection, int count)
        {
            try
            {
                var req = RequestBase.Parser.ParseFrom(connection.Buffer, 0, count);
                var resp = ResponseBase(true);

                switch (req.Type)
                {
                case MessageID.UserLogin:
                    {
                        var result = Login(req.DataUserLogin.Token).WaitResult();
                        if (result != null)
                        {
                            connection.RespondFailure(result);
                            return;
                        }
                    }
                    break;
                case MessageID.UserLogout:
                    {
                        var result = Logout();
                        if (result != null)
                        {
                            connection.RespondFailure(result);
                            return;
                        }
                    }
                    break;
                case MessageID.UserInfo:
                    lock (UserInfo)
                    {
                        resp.DataUser = UserInfo;
                    }
                    break;
                case MessageID.LogGet:
                    resp.DataLog = new LogList();
                    resp.DataLog.Data.Add(LogManager);
                    break;
                case MessageID.LogClear:
                    LogManager.Clear();
                    break;
                case MessageID.ControlExit:
                    Stop();
                    return;
                case MessageID.ControlConfigGet:
                    resp.DataConfig = new ServiceConfig()
                    {
                        BypassProxy = Natfrp.BypassProxy,
                        UpdateInterval = UpdateManager.UpdateInterval
                    };
                    break;
                case MessageID.ControlConfigSet:
                    Natfrp.BypassProxy = req.DataConfig.BypassProxy;
                    UpdateManager.UpdateInterval = req.DataConfig.UpdateInterval;
                    Save();
                    break;
                case MessageID.ControlCheckUpdate:
                    resp.DataUpdate = UpdateManager.CheckUpdate().WaitResult();
                    break;
                case MessageID.ControlGetUpdate:
                    resp.DataUpdate = UpdateManager.Status;
                    break;
                default:
                    // Login required ↓
                    lock (UserInfo)
                    {
                        if (UserInfo.Status != UserStatus.LoggedIn)
                        {
                            connection.RespondFailure("用户未登录");
                            return;
                        }
                    }
                    switch (req.Type)
                    {
                    case MessageID.TunnelList:
                        resp.DataTunnels = new TunnelList();
                        resp.DataTunnels.Tunnels.Add(TunnelManager.Values.Select(t => t.CreateProto()));
                        break;
                    case MessageID.TunnelReload:
                        TunnelManager.UpdateTunnels().Wait();
                        break;
                    case MessageID.TunnelUpdate:
                        lock (TunnelManager)
                        {
                            if (!TunnelManager.TryGetValue(req.DataUpdateTunnel.Id, out Tunnel t))
                            {
                                connection.RespondFailure("隧道不存在");
                                return;
                            }
                            t.Enabled = req.DataUpdateTunnel.Status == 1;
                            TunnelManager.PushOne(t);
                        }
                        break;
                    case MessageID.TunnelDelete:
                        Natfrp.Request<Natfrp.ApiResponse>("delete_tunnel", "tunnel=" + req.DataId).WaitResult();
                        lock (TunnelManager)
                        {
                            TunnelManager.Remove(req.DataId);
                            TunnelManager.Push();
                        }
                        break;
                    case MessageID.TunnelCreate:
                        {
                            var result = Natfrp.Request<Natfrp.CreateTunnel>("create_tunnel", new StringBuilder()
                                .Append("type=").Append(req.DataCreateTunnel.Type)
                                .Append("&name=").Append(req.DataCreateTunnel.Name)
                                .Append("&note=").Append(req.DataCreateTunnel.Note)
                                .Append("&node=").Append(req.DataCreateTunnel.Node)
                                .Append("&local_ip=").Append(req.DataCreateTunnel.LocalAddress)
                                .Append("&local_port=").Append(req.DataCreateTunnel.LocalPort)
                                .Append("&remote_port=").Append(req.DataCreateTunnel.RemotePort)
                                .Append("&encryption=").Append(req.DataCreateTunnel.Encryption ? "true" : "false")
                                .Append("&compression=").Append(req.DataCreateTunnel.Compression ? "true" : "false").ToString()).WaitResult();
                            var t = TunnelManager.CreateFromApi(result.Data);
                            lock (TunnelManager)
                            {
                                TunnelManager[t.Id] = t;
                                TunnelManager.Push();
                            }
                            resp.Message = "#" + t.Id + " " + t.Name;
                        }
                        break;
                    case MessageID.NodeList:
                        resp.DataNodes = new NodeList();
                        resp.DataNodes.Nodes.Add(NodeManager.Values);
                        break;
                    case MessageID.NodeReload:
                        NodeManager.UpdateNodes().Wait();
                        break;
                    }
                    break;
                }
                connection.SendProto(resp);
            }
            catch (AggregateException e) when (e.InnerExceptions.Count == 1)
            {
                connection.SendProto(ResponseBase(false, e.InnerExceptions[0].ToString()));
            }
            catch (Exception e)
            {
                connection.SendProto(ResponseBase(false, e.ToString()));
            }
        }

        #endregion
    }
}
