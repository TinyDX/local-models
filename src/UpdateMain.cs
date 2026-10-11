// UpdateMain.cs -- 更新器入口
using System;
using System.IO;
using System.Windows.Forms;

internal static class UpdateMain
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string newVer = AppInfo.Version;

        // ---- 找安装目录 ----
        //   支持命令行直接指定：LocalModels-Update.exe /dir "<安装目录>"
        string dir = "";
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("/dir", StringComparison.OrdinalIgnoreCase))
                dir = args[i + 1].Trim('"');

        if (dir.Length == 0) dir = UpdaterCore.RegistryDir();
        if (dir.Length == 0) dir = UpdaterCore.SiblingDir();

        if (!UpdaterCore.LooksInstalled(dir))
        {
            // 自动找不到就问用户
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "没有自动找到 Local Models 的安装目录，请手动选择" +
                                  "（里面应该有 Local Models.exe）";
                if (Directory.Exists(dir)) dlg.SelectedPath = dir;
                if (dlg.ShowDialog() != DialogResult.OK || dlg.SelectedPath.Length == 0)
                    return;
                dir = dlg.SelectedPath;
            }
            if (!UpdaterCore.LooksInstalled(dir))
            {
                MessageBox.Show(
                    "这个目录里没有 Local Models.exe，不像是安装目录。\r\n\r\n" +
                    "选中的是：\r\n" + dir + "\r\n\r\n" +
                    "请选择安装 Local Models 的那个文件夹。",
                    "Local Models 更新程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        string oldVer = UpdaterCore.InstalledVersion(dir);
        Application.Run(new UpdaterForm(dir, oldVer, newVer));
    }
}
