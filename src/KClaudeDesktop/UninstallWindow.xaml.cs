using System.Windows;
using KClaudeDesktop.Core;

namespace KClaudeDesktop;

public partial class UninstallWindow : Window
{
    private readonly InstallService _installer;

    public UninstallWindow(InstallService installer)
    {
        InitializeComponent();
        _installer = installer;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var purgeData = PurgeDataCheck.IsChecked == true;
        if (purgeData)
        {
            var answer = MessageBox.Show(
                this,
                "将永久删除 KClaude Desktop 保存的两类 DPAPI Key、设置、日志和 CLI 备份。此操作不可恢复，但不会删除 Claude Code CLI 或共享 Claude 配置。继续吗？",
                "确认彻底清理",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            _installer.ScheduleUninstall(purgeData);
            MessageBox.Show(
                this,
                purgeData ? "卸载与彻底清理将在程序退出后完成。" : "卸载将在程序退出后完成，个人数据将保留。",
                "卸载已安排",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Application.Current.Shutdown(0);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法卸载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
