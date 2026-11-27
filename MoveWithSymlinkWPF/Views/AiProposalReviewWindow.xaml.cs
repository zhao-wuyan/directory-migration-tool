using System.Windows;
using System.Windows.Input;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

/// <summary>
/// AI 建议统一审阅与批量添加弹窗（承接迁移建议、AI 疑似残留、清理规则建议、L2 引导与红线拦截）。
/// </summary>
public partial class AiProposalReviewWindow : Window
{
    public AiProposalReviewViewModel ViewModel { get; }

    public AiProposalReviewWindow(AiProposalReviewViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ViewModel.RequestCloseWindow = Close;
        DataContext = ViewModel;
        Closed += (_, _) => ViewModel.CleanupSubscriptions();
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
