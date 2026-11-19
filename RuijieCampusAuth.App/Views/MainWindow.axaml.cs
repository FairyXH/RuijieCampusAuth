using Avalonia.Controls;

namespace RuijieCampusAuth.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 认证日志始终贴住最新一行：GUI 与 [服务] 输出都在追加，默认跟随底部。
        var authLog = this.FindControl<TextBox>("AuthLog");
        if (authLog is not null)
        {
            authLog.TextChanged += (_, _) =>
            {
                // 把光标钉到末尾，TextBox 会自动滚动到可见区域。
                authLog.CaretIndex = authLog.Text?.Length ?? 0;
            };
        }
    }
}