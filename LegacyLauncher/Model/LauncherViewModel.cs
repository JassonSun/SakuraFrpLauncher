﻿using System;
using System.IO;
using System.Windows.Forms;

using SakuraLibrary;
using SakuraLibrary.Model;
using SakuraLibrary.Proto;
using SakuraLibrary.Helper;

namespace LegacyLauncher.Model
{
    public class LauncherViewModel : LauncherModel
    {
        public readonly Func<string, bool> SimpleConfirmHandler;
        public readonly Action<bool, string> SimpleHandler;
        public readonly Action<bool, string> SimpleFailureHandler;

        public readonly MainForm View;

        public LauncherViewModel(MainForm view)
        {
            SimpleConfirmHandler = message => MessageBox.Show(View, message, "操作确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Asterisk) == DialogResult.OK;
            SimpleHandler = (success, message) => MessageBox.Show(View, message, success ? "操作成功" : "操作失败", MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            SimpleFailureHandler = (success, message) =>
            {
                if (!success)
                {
                    MessageBox.Show(View, message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            View = view;
            Dispatcher = new DispatcherWrapper(a => View.Invoke(a), a => View.BeginInvoke(a), () => !View.InvokeRequired);

            var settings = Properties.Settings.Default;
            if (settings.UpgradeRequired)
            {
                settings.Upgrade();
                settings.UpgradeRequired = false;
                settings.Save();
            }

            SuppressInfo = settings.SuppressInfo;
            LogTextWrapping = settings.LogTextWrapping;
        }

        public override void ClearLog() => Dispatcher.Invoke(() => View.textBox_log.Clear());

        public override void Log(Log l, bool init)
        {
            string category = "";
            switch (l.Category)
            {
            case 1:
                category = "INFO ";
                break;
            case 2:
                category = "WARNING ";
                break;
            case 3:
                category = "ERROR ";
                break;
            }
            Dispatcher.Invoke(() => View.textBox_log.AppendText(l.Source + " " + category + l.Data + Environment.NewLine));
        }

        public override void Save()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Save());
                return;
            }
            var settings = Properties.Settings.Default;

            settings.SuppressInfo = SuppressInfo;
            settings.LogTextWrapping = LogTextWrapping;

            settings.Save();
        }
    }
}
