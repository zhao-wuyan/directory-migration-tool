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

    public static readonly DependencyProperty ShowSessionSwitcherProperty = DependencyProperty.Register(
        nameof(ShowSessionSwitcher),
        typeof(bool),
        typeof(AiAssistantDrawerControl),
        new PropertyMetadata(false));

    public bool ShowCloseButton
    {
        get => (bool)GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    public bool ShowSessionSwitcher
    {
        get => (bool)GetValue(ShowSessionSwitcherProperty);
        set => SetValue(ShowSessionSwitcherProperty, value);
    }

    /// <summary>
    /// 是否处于自动贴底跟踪状态：滚动条在底部时随流式输出自动保持底部；用户向上翻看历史时自动暂停，滚回底部或发送新消息后恢复。
    /// </summary>
    private bool _stickToBottom = true;
    private INotifyCollectionChanged? _subscribedTurns;

    public AiAssistantDrawerControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachTurns();
        INotifyCollectionChanged? turns = e.NewValue switch
        {
            AiChatSessionViewModel session => session.ConversationTurns,
            AiAssistantViewModel vm => vm.ConversationTurns,
            _ => null
        };

        if (turns is not null)
        {
            _subscribedTurns = turns;
            _subscribedTurns.CollectionChanged += ConversationTurns_OnCollectionChanged;
            _stickToBottom = true;
            ChatScrollViewer.ScrollToEnd();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachTurns();

    private void DetachTurns()
    {
        if (_subscribedTurns is not null)
        {
            _subscribedTurns.CollectionChanged -= ConversationTurns_OnCollectionChanged;
            _subscribedTurns = null;
        }
    }

    private void ConversationTurns_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
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
            return (currentStickToBottom, currentStickToBottom);
        }

        if (Math.Abs(extentHeightChange) <= 0.5)
        {
            bool atBottom = verticalOffset >= scrollableHeight - Math.Max(0, tolerance);
            return (atBottom, false);
        }

        bool stillAtBottom = currentStickToBottom || verticalOffset >= scrollableHeight - Math.Max(0, tolerance);
        return (stillAtBottom, false);
    }

    private void ReasoningEffortSlider_OnCloseRequested(object? sender, EventArgs e)
    {
        ReasoningEffortToggle.IsChecked = false;
    }

    private void PromptInput_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (DataContext is AiChatSessionViewModel session && session.CanSendPrompt)
        {
            e.Handled = true;
            if (session.SendPromptCommand.CanExecute(null))
            {
                session.SendPromptCommand.Execute(null);
            }
        }
        else if (DataContext is AiAssistantViewModel vm && vm.CanSendPrompt)
        {
            e.Handled = true;
            if (vm.SendPromptCommand.CanExecute(null))
            {
                vm.SendPromptCommand.Execute(null);
            }
        }
    }
}
