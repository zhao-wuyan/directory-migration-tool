using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class AiAssistantDrawerControl : UserControl
{
    private const double BottomStickTolerance = 24d;

    public static readonly DependencyProperty ShowCloseButtonProperty = DependencyProperty.Register(
        nameof(ShowCloseButton),
        typeof(bool),
        typeof(AiAssistantDrawerControl),
        new PropertyMetadata(true));

    public bool ShowCloseButton
    {
        get => (bool)GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    /// <summary>
    /// 是否处于自动贴底跟踪状态：滚动条在底部时随流式输出自动保持底部；用户向上翻看历史时自动暂停，滚回底部或发送新消息后恢复。
    /// </summary>
    private bool _stickToBottom = true;
    private AiAssistantViewModel? _subscribedVm;

    public AiAssistantDrawerControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachVm();
        if (e.NewValue is AiAssistantViewModel vm)
        {
            _subscribedVm = vm;
            _subscribedVm.ConversationTurns.CollectionChanged += ConversationTurns_OnCollectionChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachVm();

    private void DetachVm()
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.ConversationTurns.CollectionChanged -= ConversationTurns_OnCollectionChanged;
            _subscribedVm = null;
        }
    }

    private void ConversationTurns_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            // 用户发送新提问时立即重新吸底，确保新消息与实时回复始终可见
            _stickToBottom = true;
            ChatScrollViewer.ScrollToEnd();
        }
    }

    private void ChatScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool shouldScrollToEnd;
        (_stickToBottom, shouldScrollToEnd) = ComputeNextStickToBottomState(
            _stickToBottom,
            e.ExtentHeightChange,
            ChatScrollViewer.VerticalOffset,
            ChatScrollViewer.ScrollableHeight,
            BottomStickTolerance);

        if (shouldScrollToEnd)
        {
            ChatScrollViewer.ScrollToEnd();
        }
    }

    /// <summary>
    /// 纯状态机：根据内容高度变化量与当前滚动条距底部距离计算下一刻的贴底状态及是否需要自动滚到底部。
    /// </summary>
    public static (bool StickToBottom, bool ShouldScrollToEnd) ComputeNextStickToBottomState(
        bool currentStickToBottom,
        double extentHeightChange,
        double verticalOffset,
        double scrollableHeight,
        double tolerance = BottomStickTolerance)
    {
        if (scrollableHeight <= 1.0)
        {
            return (true, false);
        }

        if (extentHeightChange > 0.5)
        {
            // 内容增长（流式打字/工具项展开）：仅当原本贴底时自动跟到底部，绝不打断用户向上翻看历史
            return (currentStickToBottom, currentStickToBottom);
        }

        if (Math.Abs(extentHeightChange) <= 0.5)
        {
            // 用户主动滚轮/拖拽滚动条或窗口高度变更：检查是否位于底部容差内
            bool atBottom = verticalOffset >= scrollableHeight - Math.Max(0, tolerance);
            return (atBottom, false);
        }

        // 内容变短（折叠详情或清空）：若本身贴底或已经到底部，保持贴底
        bool stillAtBottom = currentStickToBottom || verticalOffset >= scrollableHeight - Math.Max(0, tolerance);
        return (stillAtBottom, false);
    }

    private void PromptInput_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
            && DataContext is AiAssistantViewModel vm
            && vm.CanSendPrompt)
        {
            e.Handled = true;
            if (vm.SendPromptCommand.CanExecute(null))
            {
                vm.SendPromptCommand.Execute(null);
            }
        }
    }
}
