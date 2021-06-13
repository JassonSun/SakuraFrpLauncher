﻿using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

using SakuraLibrary;

namespace LegacyLauncher
{
    static class Program
    {
        public static Mutex AppMutex = null;
        public static Form TopMostForm => new Form() { TopMost = true };

        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(UtilsWindows.ExecutablePath);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            UtilsWindows.VerifySignature(UtilsWindows.LibraryPath, UtilsWindows.ExecutablePath, Path.GetFullPath(Consts.ServiceExecutable));
            UtilsWindows.ValidateSettings();

            var minimize = false;
            foreach (var a in args)
            {
                var split = a.Split('=');
                if (split[0] == "--minimize")
                {
                    minimize = true;
                }
            }

            AppMutex = new Mutex(true, "LegacySakuraLauncher_" + UtilsWindows.InstallationHash, out bool created);

            if (created)
            {
                Application.Run(new MainForm(minimize));
            }
            else
            {
                MessageBox.Show(TopMostForm, "请不要重复开启 SakuraFrp 客户端. 如果想运行多个实例请将软件复制到其他目录.", "Oops", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Environment.Exit(0);
            }

            AppMutex.ReleaseMutex();
        }
    }
}
