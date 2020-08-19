﻿using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using System.Windows.Media.Animation;

using fastJSON;
using Microsoft.Win32;
using SakuraLibrary.Proto;
using MaterialDesignThemes.Wpf;

using SakuraLauncher.View;
using SakuraLauncher.Data;
using SakuraLauncher.Helper;

namespace SakuraLauncher
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public const int CONFIG_VERSION = 2;

        public static ulong Tick = 0;
        public static MainWindow Instance = null;
        
        public string ConfigPath = null;

        public bool CanEditToken => !LoggingIn.Value && !LoggedIn.Value;

        public Prop<bool> AutoRun { get; set; } = new Prop<bool>();
        public Prop<bool> SuppressInfo { get; set; } = new Prop<bool>();
        public Prop<bool> LogTextWrapping { get; set; } = new Prop<bool>(true);
        public Prop<bool> BypassProxy { get; set; } = new Prop<bool>(true);
        public Prop<string> UserToken { get; set; } = new Prop<string>("");

        public Prop<bool> CheckUpdate { get; set; } = new Prop<bool>(true);
        public Prop<bool> CheckingUpdate { get; set; } = new Prop<bool>();

        public Prop<bool> LoggedIn { get; set; } = new Prop<bool>();
        public Prop<bool> LoggingIn { get; set; } = new Prop<bool>();

        public UserControl[] Tabs = null;
        public TabIndexTester CurrentTabTester { get; set; }
        public Prop<int> CurrentTab { get; set; } = new Prop<int>();

        public User UserInfo = null;
        
        public List<string> AutoStart = null;
        public ObservableCollection<NodeData> Nodes { get; set; } = new ObservableCollection<NodeData>();
        public ObservableCollection<ITunnelModel> Tunnels { get; set; } = new ObservableCollection<ITunnelModel>();

        public int LogoIndex = 0;

        public MainWindow(bool autorun)
        {
            Instance = this;

            AutoRun.Value = autorun;
            CurrentTabTester = new TabIndexTester(this);

            Tabs = new UserControl[] {
                new TunnelTab(this),
                new LogTab(this),
                new SettingsTab(this),
                new AboutTab()
            };

            InitializeComponent();

            LoggedIn.PropertyChanged += (s, e) => RaisePropertyChanged("CanEditToken");
            LoggingIn.PropertyChanged += (s, e) => RaisePropertyChanged("CanEditToken");

            #region Load Config

            if(File.Exists("config.json"))
            {
                var json = JSON.ToObject<Dictionary<string, dynamic>>(File.ReadAllText("config.json"));
                if(json.ContainsKey("token"))
                {
                    UserToken.Value = json["token"];
                }
                if(json.ContainsKey("logo"))
                {
                    SetLogo((int)json["logo"]);
                }
                if (json.ContainsKey("width"))
                {
                    Width = json["width"];
                }
                if (json.ContainsKey("height"))
                {
                    Height = json["height"];
                }
                if (json.ContainsKey("suppressinfo") && json["suppressinfo"])
                {
                    SuppressInfo.Value = true;
                }
                if(!json.ContainsKey("log_text_wrapping") || json["log_text_wrapping"])
                {
                    LogTextWrapping.Value = true;
                }
                if (!json.ContainsKey("bypass_proxy") || json["bypass_proxy"])
                {
                    BypassProxy.Value = true;
                }
                if (!json.ContainsKey("check_update") || json["check_update"])
                {
                    CheckUpdate.Value = true;
                }
                if (json.ContainsKey("enable_tunnels") && json["enable_tunnels"] is List<object> enable_tunnels)
                {
                    AutoStart = enable_tunnels.Select(s => s.ToString()).ToList();
                }
                if(json.ContainsKey("loggedin") && json["loggedin"])
                {
                    // TODO
                }
            }
            ConfigPath = "config.json";

            #endregion

            DataContext = this;
            SuppressInfo.PropertyChanged += (s, e) => Save();

            SwitchTab(LoggedIn.Value || LoggingIn.Value ? 0 : 2);

            SystemEvents.SessionEnding += (s, e) =>
            {
                ConfigPath = null;
            };

            if (CheckUpdate)
            {
                TryCheckUpdate(true);
            }
        }
        
        public void Save()
        {
            if(ConfigPath == null)
            {
                return;
            }
            File.WriteAllText(ConfigPath, JSON.ToNiceJSON(new Dictionary<string, object>()
            {
                { "version", CONFIG_VERSION },
                { "logo", LogoIndex },
                { "width", Dispatcher.Invoke(() => (int)Width) },
                { "height", Dispatcher.Invoke(() => (int)Height) },
                { "token", UserToken.Value.Trim() },
                { "suppressinfo", SuppressInfo.Value },
                { "log_text_wrapping", LogTextWrapping.Value },
                { "bypass_proxy", BypassProxy.Value },
                { "check_update", CheckUpdate.Value },
                { "loggedin", LoggedIn.Value }
            }));
        }
        
        public void TryCheckUpdate(bool silent = false)
        {
            if (!File.Exists("SakuraUpdater.exe"))
            {
                if (!silent)
                {
                    App.ShowMessage("自动更新程序不存在, 无法进行更新检查", "Oops", MessageBoxImage.Error);
                }
                return;
            }
            CheckingUpdate.Value = true;
            App.ApiRequest("get_version", "legacy=false").ContinueWith(t =>
            {
                try
                {
                    var version = t.Result;
                    if (version == null)
                    {
                        return;
                    }
                    var sb = new StringBuilder();
                    bool launcher_update = false, frpc_update = false;
                    if (Assembly.GetExecutingAssembly().GetName().Version.CompareTo(Version.Parse(version["launcher"]["version"] as string)) < 0)
                    {
                        launcher_update = true;
                        sb.Append("启动器最新版: ")
                            .AppendLine(version["launcher"]["version"] as string)
                            .AppendLine("更新日志:")
                            .AppendLine(version["launcher"]["note"] as string)
                            .AppendLine();
                    }

                    var temp = (version["frpc"]["version"] as string).Split(new string[] { "-sakura-" }, StringSplitOptions.None);
                    if (App.FrpcVersion.CompareTo(Version.Parse(temp[0])) < 0 || App.FrpcVersionSakura < float.Parse(temp[1]))
                    {
                        frpc_update = true;
                        sb.Append("frpc 最新版: ")
                            .AppendLine(version["frpc"]["version"] as string)
                            .AppendLine("更新日志:")
                            .AppendLine(version["frpc"]["note"] as string);
                    }

                    if (!launcher_update && !frpc_update)
                    {
                        if(!silent)
                        {
                            App.ShowMessage("您当前使用的启动器和 frpc 均为最新版本", "提示", MessageBoxImage.Information);
                        }
                    }
                    else if (App.ShowMessage(sb.ToString(), "发现新版本, 是否更新", MessageBoxImage.Asterisk, MessageBoxButton.OKCancel) == MessageBoxResult.OK)
                    {
                        ConfigPath = null;
                        foreach (var l in Tunnels)
                        {
                            if (l.IsReal)
                            {
                                l.Real.Stop();
                            }
                        }
                        Process.Start("SakuraUpdater.exe", (launcher_update ? "-launcher" : "") + (frpc_update ? " -frpc" : ""));
                        Environment.Exit(0);
                    }
                }
                catch (Exception e)
                {
                    if (!silent)
                    {
                        App.ShowMessage("检查更新出错:\n" + e.ToString(), "Oops", MessageBoxImage.Error);
                    }
                }
                finally
                {
                    Dispatcher.Invoke(() => CheckingUpdate.Value = false);
                }
            });
        }
        
        public void SetLogo(int index)
        {
            switch(index)
            {
            case 1:
                logo.Background = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/Resources/debian.png")));
                break;
            case 2:
                logo.Background = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/Resources/fishcake.png")));
                break;
            default:
                logo.Background = new ImageBrush(new BitmapImage(new Uri("pack://application:,,,/Resources/logo.png")));
                break;
            }
            LogoIndex = index;
            Save();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => Save();

        private void Window_Closed(object sender, EventArgs e)
        {
            ConfigPath = null;
            // TODO
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            App.ReleaseCapture();
            App.SendMessage(new WindowInteropHelper(this).Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
        }

        private void ButtonHide_Click(object sender, RoutedEventArgs e) => Hide();

        private void ButtonClose_Click(object sender, RoutedEventArgs e) => Close();

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            switch(e.ClickCount)
            {
            case 3:
                SetLogo(1);
                break;
            case 5:
                SetLogo(2);
                break;
            }
        }

        private void TrayIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e)
        {
            Show();
            Topmost = true; // Still using this in 2020?
            Topmost = false;
        }

        #region Tab Switching

        public void SwitchTab(int id)
        {
            CurrentTab.Value = id;
            tabContents.BeginStoryboard(Resources["TabHideAnimation"] as Storyboard);
        }

        private void ButtonTab_Click(object sender, RoutedEventArgs e)
        {
            int id = int.Parse((sender as Button).Tag as string);
            if(CurrentTab.Value != id)
            {
                SwitchTab(id);
                RaisePropertyChanged("CurrentTabTester");
            }
        }

        private void StoryboardTabHideAnimation_Completed(object sender, EventArgs e)
        {
            tabContents.Child = Tabs[CurrentTab.Value];
            tabContents.BeginStoryboard(Resources["TabShowAnimation"] as Storyboard);
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        public void RaisePropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion
    }
}
