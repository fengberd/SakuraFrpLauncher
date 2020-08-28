﻿using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using SakuraLibrary.Pipe;
using SakuraLibrary.Proto;
using SakuraLibrary.Helper;

using UserStatus = SakuraLibrary.Proto.User.Types.Status;
using System.IO;

namespace SakuraLibrary.Model
{
    public abstract class LauncherModel : ModelBase, IAsyncManager
    {
        public readonly PipeClient Pipe = new PipeClient(Utils.InstallationPipeName);
        public readonly DaemonHost Daemon;
        public readonly AsyncManager AsyncManager;

        public LauncherModel()
        {
            Daemon = new DaemonHost(this);
            AsyncManager = new AsyncManager(Run);

            Pipe.ServerPush += Pipe_ServerPush;

            Daemon.Start();
            Start();
        }

        public abstract void Log(Log l, bool init = false);

        public abstract void Save();
        public abstract void Load();

        public virtual bool Refresh()
        {
            // TODO: May merge the following request into single one
            var logs = Pipe.Request(MessageID.LogGet);
            var config = Pipe.Request(MessageID.ControlConfigGet);
            var update = Pipe.Request(MessageID.ControlGetUpdate);
            if (!logs.Success || !config.Success || !update.Success)
            {
                return false;
            }
            Dispatcher.Invoke(() =>
            {
                foreach (var l in logs.DataLog.Data)
                {
                    Log(l, true);
                }
                Config = config.DataConfig;
                Update = update.DataUpdate;
            });

            var nodes = Pipe.Request(MessageID.NodeList);
            if (nodes.Success)
            {
                Dispatcher.Invoke(() =>
                {
                    Nodes.Clear();
                    foreach (var n in nodes.DataNodes.Nodes)
                    {
                        Nodes.Add(new NodeModel(n));
                    }
                    Config = config.DataConfig;
                });
            }

            var tunnels = Pipe.Request(MessageID.TunnelList);
            if (tunnels.Success)
            {
                LoadTunnels(tunnels.DataTunnels);
            }
            return true;
        }

        #region IPC Handling

        protected void Run()
        {
            do
            {
                lock (Pipe)
                {
                    if (Pipe.Connected)
                    {
                        continue;
                    }
                    Connected = false;

                    if (!Pipe.Connect())
                    {
                        continue;
                    }

                    var user = Pipe.Request(MessageID.UserInfo);
                    if (!user.Success)
                    {
                        Pipe.Dispose();
                        continue;
                    }
                    UserInfo = user.DataUser;

                    if (!Refresh())
                    {
                        Pipe.Dispose();
                        continue;
                    }

                    Connected = true;
                }
            }
            while (!AsyncManager.StopEvent.WaitOne(500));
        }

        protected void Pipe_ServerPush(PipeConnection connection, int length)
        {
            try
            {
                var msg = PushMessageBase.Parser.ParseFrom(connection.Buffer, 0, length);
                switch (msg.Type)
                {
                case PushMessageID.UpdateUser:
                    UserInfo = msg.DataUser;
                    break;
                case PushMessageID.UpdateTunnel:
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var t in Tunnels)
                        {
                            if (t.Id == msg.DataTunnel.Id)
                            {
                                t.Proto = msg.DataTunnel;
                                t.SetNodeName(Nodes.ToDictionary(k => k.Id, v => v.Name));
                                break;
                            }
                        }
                    });
                    break;
                case PushMessageID.UpdateTunnels:
                    LoadTunnels(msg.DataTunnels);
                    break;
                case PushMessageID.UpdateNodes:
                    Dispatcher.Invoke(() =>
                    {
                        Nodes.Clear();
                        var map = new Dictionary<int, string>();
                        foreach (var n in msg.DataNodes.Nodes)
                        {
                            Nodes.Add(new NodeModel(n));
                            map.Add(n.Id, n.Name);
                        }
                        foreach (var t in Tunnels)
                        {
                            t.SetNodeName(map);
                        }
                    });
                    break;
                case PushMessageID.AppendLog:
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var l in msg.DataLog.Data)
                        {
                            Log(l);
                        }
                    });
                    break;
                case PushMessageID.PushUpdate:
                    Update = msg.DataUpdate;
                    break;
                }
            }
            catch { }
        }

        #endregion

        #region Main Window

        public bool Connected { get => _connected; set => Set(out _connected, value); }
        private bool _connected = false;

        public User UserInfo
        {
            get => _userInfo;
            set
            {
                if (value == null)
                {
                    value = new User();
                }
                SafeSet(out _userInfo, value);
            }
        }
        private User _userInfo = new User();

        public int CurrentTab { get => _currentTab; set => Set(out _currentTab, value); }
        private int _currentTab = -1;

        public ObservableCollection<NodeModel> Nodes { get; set; } = new ObservableCollection<NodeModel>();

        #endregion

        #region Tunnel

        public ObservableCollection<TunnelModel> Tunnels { get; set; } = new ObservableCollection<TunnelModel>();

        public void RequestReloadTunnels(Action<bool, string> callback) => ThreadPool.QueueUserWorkItem(s =>
         {
             try
             {
                 var resp = Pipe.Request(MessageID.TunnelReload);
                 if (resp.DataTunnels != null)
                 {
                     LoadTunnels(resp.DataTunnels);
                 }
                 callback(resp.Success, resp.Message);
             }
             catch (Exception e)
             {
                 callback(false, e.ToString());
             }
         });

        public void RequestDeleteTunnel(int id, Action<bool, string> callback) => ThreadPool.QueueUserWorkItem(s =>
        {
            try
            {
                var resp = Pipe.Request(new RequestBase()
                {
                    Type = MessageID.TunnelDelete,
                    DataId = id
                });
                callback(resp.Success, resp.Message);
            }
            catch (Exception e)
            {
                callback(false, e.ToString());
            }
        });

        protected void LoadTunnels(TunnelList list) => Dispatcher.Invoke(() =>
        {
            Tunnels.Clear();
            var map = Nodes.ToDictionary(k => k.Id, v => v.Name);
            foreach (var t in list.Tunnels)
            {
                Tunnels.Add(new TunnelModel(t, this, map));
            }
        });

        #endregion

        #region Settings

        // User Status

        [SourceBinding(nameof(UserInfo))]
        public string UserToken { get => UserInfo.Status != UserStatus.NoLogin ? "****************" : _userToken; set => SafeSet(out _userToken, value); }
        private string _userToken = "";

        [SourceBinding(nameof(UserInfo))]
        public bool LoggedIn => UserInfo.Status == UserStatus.LoggedIn;

        [SourceBinding(nameof(LoggingIn))]
        public bool TokenEditable => !LoggingIn;

        [SourceBinding(nameof(UserInfo))]
        public bool LoggingIn { get => _loggingIn || UserInfo.Status == UserStatus.Pending; set => SafeSet(out _loggingIn, value); }
        private bool _loggingIn;

        public void RequestLogin(Action<bool, string> callback)
        {
            LoggingIn = true;
            ThreadPool.QueueUserWorkItem(s =>
            {
                try
                {
                    var resp = Pipe.Request(LoggedIn ? new RequestBase()
                    {
                        Type = MessageID.UserLogout
                    } : new RequestBase()
                    {
                        Type = MessageID.UserLogin,
                        DataUserLogin = new UserLogin()
                        {
                            Token = UserToken
                        }
                    });
                    callback(resp.Success, resp.Message);
                    Refresh();
                }
                catch (Exception e)
                {
                    callback(false, e.ToString());
                }
                finally
                {
                    LoggingIn = false;
                }
            });
        }

        // Launcher Config

        public bool SuppressInfo { get => _suppressInfo; set => Set(out _suppressInfo, value); }
        private bool _suppressInfo;

        public bool LogTextWrapping { get => _logTextWrapping; set => Set(out _logTextWrapping, value); }
        private bool _logTextWrapping;

        // Service Config

        public ServiceConfig Config { get => _config; set => SafeSet(out _config, value); }
        private ServiceConfig _config;

        [SourceBinding(nameof(Config))]
        public bool BypassProxy
        {
            get => Config != null && Config.BypassProxy;
            set
            {
                if (Config != null)
                {
                    Config.BypassProxy = value;
                    PushServiceConfig();
                }
                RaisePropertyChanged();
            }
        }

        public ResponseBase PushServiceConfig() => Pipe.Request(new RequestBase()
        {
            Type = MessageID.ControlConfigSet,
            DataConfig = Config
        });

        // Update Checking

        [SourceBinding(nameof(Config))]
        public bool RemoteManagement
        {
            get => Config != null && Config.RemoteManagement;
            set
            {
                if (Config != null)
                {
                    if (!value || Config.RemoteKeySet)
                    {
                        Config.RemoteManagement = value;
                    }
                    PushServiceConfig();
                }
                RaisePropertyChanged();
            }
        }

        [SourceBinding(nameof(Config))]
        public bool CanEnableRemoteManagement => Config != null && Config.RemoteKeySet;

        public UpdateStatus Update { get => _update; set => SafeSet(out _update, value); }
        private UpdateStatus _update;

        [SourceBinding(nameof(Update))]
        public bool HaveUpdate => Update != null && (Update.UpdateFrpc || Update.UpdateLauncher);

        [SourceBinding(nameof(Config))]
        public bool CheckUpdate
        {
            get => Config != null && Config.UpdateInterval != -1;
            set
            {
                if (Config != null)
                {
                    Config.UpdateInterval = value ? 86400 : -1;
                    PushServiceConfig();
                }
                if (!value)
                {
                    Update = null;
                }
                RaisePropertyChanged();
            }
        }

        public bool CheckingUpdate { get => _checkingUpdate; set => SafeSet(out _checkingUpdate, value); }
        private bool _checkingUpdate;

        public void RequestUpdateCheck(Action<UpdateStatus> callback)
        {
            if (CheckingUpdate)
            {
                return;
            }
            CheckingUpdate = true;
            ThreadPool.QueueUserWorkItem(s =>
            {
                try
                {
                    var resp = Pipe.Request(MessageID.ControlCheckUpdate);
                    if (resp.DataUpdate != null)
                    {
                        Update = resp.DataUpdate;
                    }
                    callback(Update);
                }
                finally
                {
                    CheckingUpdate = false;
                }
            });
        }

        public void DoUpdate(bool legacy, Action<bool, string> callback)
        {
            if (!File.Exists("Updater.exe"))
            {
                callback(false, "更新程序不存在, 操作无法继续\n请重新下载完整的启动器手动覆盖当前版本");
                return;
            }
            // TODO: May check updater signature
            Daemon.Stop();
            Process.Start("Updater.exe", string.Format("{0}{1}", Update.UpdateFrpc ? "-frpc " : "", Update.UpdateLauncher ? (legacy ? "-legacy -launch=legacy" : "-wpf -launch=wpf") : ""));
            Environment.Exit(0);
        }

        // Working Mode Config

        public bool IsDaemon => Daemon.Daemon;

        public string WorkingMode => Daemon.Daemon ? "守护进程" : "系统服务";

        public bool SwitchingMode { get => _switchingMode; set => SafeSet(out _switchingMode, value); }
        private bool _switchingMode;

        public void SwitchWorkingMode(Action<bool, string> callback, Func<string, bool> confirm)
        {
            if (SwitchingMode)
            {
                return;
            }
            if (LoggingIn || LoggedIn)
            {
                callback(false, "请先登出当前账户");
                return;
            }
            if (!confirm("确定要切换运行模式吗?\n如果您不知道该操作的作用, 请不要切换运行模式\n如果您不知道该操作的作用, 请不要切换运行模式\n如果您不知道该操作的作用, 请不要切换运行模式\n\n注意事项:\n1. 切换运行模式后不要移动启动器到其他目录, 否则会造成严重错误\n2. 如需移动或卸载启动器, 请先切到 \"守护进程\" 模式来避免文件残留\n3. 切换过程可能需要十余秒, 请耐心等待, 不要做其他操作\n4. 切换操作即为 安装/卸载 系统服务, 需要管理员权限\n5. 切换完成后需要重启启动器"))
            {
                return;
            }
            SwitchingMode = true;
            ThreadPool.QueueUserWorkItem(s =>
            {
                try
                {
                    Daemon.Stop();
                    if (!Daemon.InstallService(!Daemon.Daemon))
                    {
                        callback(false, "运行模式切换失败, 请检查您是否有足够的权限 安装/卸载 服务.\n由于发生严重错误, 启动器即将退出.");
                    }
                    else
                    {
                        callback(true, "运行模式已切换, 启动器即将退出");
                    }
                    Environment.Exit(0);
                }
                finally
                {
                    SwitchingMode = false;
                }
            });
        }

        #endregion

        #region IAsyncManager

        public bool Running => AsyncManager.Running;

        public void Start() => AsyncManager.Start(true);

        public void Stop(bool kill = false) => AsyncManager.Stop(kill);

        #endregion
    }
}
