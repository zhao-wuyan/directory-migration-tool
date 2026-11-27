using System.Windows;
using System.Windows.Controls;
using MigrationCore.Models.Cleanup;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

/// <summary>
/// 回执虚拟化列表模板选择器：区分规则头行与独立明细行（兼容 VirtualizationMode=Recycling）。
/// </summary>
public sealed class CleanupReceiptEntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? RuleTemplate { get; set; }
    public DataTemplate? DetailTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        CleanupReceiptRuleEntry => RuleTemplate,
        CleanupReceiptDetailEntry => DetailTemplate,
        _ => base.SelectTemplate(item, container)
    };
}

/// <summary>
/// 垃圾清理执行完整回执弹窗（结构化 KPI 卡片 + 状态筛选 + 扁平虚拟化完整明细 + 一键复制/直达隔离区）。
/// </summary>
public partial class CleanupExecutionReceiptWindow : Window
{
    public CleanupExecutionReceiptViewModel ViewModel { get; }

    public CleanupExecutionReceiptWindow(
        CleanupExecutionResult result,
        string? windowTitle = null,
        string? plainTextReport = null,
        Action? openQuarantineCallback = null)
    {
        InitializeComponent();
        ViewModel = new CleanupExecutionReceiptViewModel(
            result: result,
            windowTitle: windowTitle,
            plainTextReport: plainTextReport,
            openQuarantineCallback: openQuarantineCallback,
            closeWindowCallback: Close);
        DataContext = ViewModel;
    }

    public static void ShowForResult(
        CleanupExecutionResult result,
        string? windowTitle = null,
        string? plainTextReport = null,
        Action? openQuarantineCallback = null)
    {
        var win = new CleanupExecutionReceiptWindow(
            result: result,
            windowTitle: windowTitle,
            plainTextReport: plainTextReport,
            openQuarantineCallback: openQuarantineCallback);

        Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current?.MainWindow;
        if (owner is not null && owner != win && owner.IsLoaded)
        {
            win.Owner = owner;
        }

        win.ShowDialog();
    }
}
