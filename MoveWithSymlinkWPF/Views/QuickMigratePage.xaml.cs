using MoveWithSymlinkWPF.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MoveWithSymlinkWPF.Views;

/// <summary>
/// QuickMigratePage.xaml 的交互逻辑
/// </summary>
public partial class QuickMigratePage : UserControl
{
    public QuickMigrateViewModel ViewModel { get; }

    private DispatcherTimer? _popupCloseTimer;
    private const int POPUP_CLOSE_DELAY_MS = 1000; // 1秒后关闭
    private Window? _parentWindow;

    public QuickMigratePage()
    {
        InitializeComponent();
        ViewModel = new QuickMigrateViewModel();
        DataContext = ViewModel;

        // 订阅加载和卸载事件
        Loaded += QuickMigratePage_Loaded;
        Unloaded += QuickMigratePage_Unloaded;
    }

    /// <summary>
    /// 页面加载时订阅主窗口事件
    /// </summary>
    private void QuickMigratePage_Loaded(object sender, RoutedEventArgs e)
    {
        // 查找父窗口
        _parentWindow = Window.GetWindow(this);
        if (_parentWindow != null)
        {
            // 订阅窗口失去焦点事件
            _parentWindow.Deactivated += ParentWindow_Deactivated;
            // 订阅鼠标按下事件（用于检测点击空白处）
            _parentWindow.PreviewMouseDown += ParentWindow_PreviewMouseDown;
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] QuickMigratePage_Loaded - 已订阅主窗口事件");
#endif
        }
    }

    /// <summary>
    /// 页面卸载时取消订阅
    /// </summary>
    private void QuickMigratePage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_parentWindow != null)
        {
            _parentWindow.Deactivated -= ParentWindow_Deactivated;
            _parentWindow.PreviewMouseDown -= ParentWindow_PreviewMouseDown;
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] QuickMigratePage_Unloaded - 已取消订阅主窗口事件");
#endif
            _parentWindow = null;
        }

        // 清理计时器
        StopCloseTimer();
    }

    /// <summary>
    /// 主窗口失去焦点时关闭所有 Popup
    /// </summary>
    private void ParentWindow_Deactivated(object? sender, EventArgs e)
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ParentWindow_Deactivated - 主窗口失去焦点，关闭所有 Popup");
#endif
        // 停止计时器
        StopCloseTimer();

        // 关闭所有打开的 Popup
        CloseAllPopups();
    }

    /// <summary>
    /// 检测鼠标点击是否在 Popup 外部，如果是则关闭 Popup
    /// </summary>
    private void ParentWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 获取所有打开的 Popup
        var openPopups = FindVisualChildren<Popup>(this).Where(p => p.IsOpen).ToList();

        if (openPopups.Count == 0)
            return;

        // 获取鼠标点击的元素
        var clickedElement = e.OriginalSource as DependencyObject;
        if (clickedElement == null)
            return;

        // 检查是否点击在任何 Popup 或其触发元素（ErrorTag）内
        bool isClickInsidePopupOrTrigger = false;

        foreach (var popup in openPopups)
        {
            // 检查是否点击在 Popup 内
            if (IsElementInsidePopup(clickedElement, popup))
            {
                isClickInsidePopupOrTrigger = true;
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] PreviewMouseDown - 点击在 Popup 内部");
#endif
                break;
            }

            // 检查是否点击在 ErrorTag（Popup 的触发元素）内
            if (popup.PlacementTarget != null && IsVisualAncestorOf(popup.PlacementTarget, clickedElement))
            {
                isClickInsidePopupOrTrigger = true;
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] PreviewMouseDown - 点击在 ErrorTag 内部");
#endif
                break;
            }
        }

        // 如果点击在外部，关闭所有 Popup
        if (!isClickInsidePopupOrTrigger)
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] PreviewMouseDown - 点击空白处，关闭所有 Popup");
#endif
            StopCloseTimer();
            CloseAllPopups();
        }
    }

    /// <summary>
    /// 关闭页面上所有打开的 Popup
    /// </summary>
    private void CloseAllPopups()
    {
        var popups = FindVisualChildren<Popup>(this).Where(p => p.IsOpen).ToList();
        foreach (var popup in popups)
        {
            popup.IsOpen = false;
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] CloseAllPopups - 已关闭 Popup: {popup.Name}");
#endif
        }
    }

    #region Popup Error Details Event Handlers

    /// <summary>
    /// 点击错误标签时复制错误信息（调用 ViewModel 方法）
    /// </summary>
    private async void ErrorTag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseLeftButtonDown - 点击错误标签");
#endif

        if (sender is FrameworkElement element && element.DataContext is MigrationCore.Models.QuickMigrateTask task)
        {
            // 先关闭Popup
            var popup = FindPopupForElement(element);
            if (popup != null)
            {
                popup.IsOpen = false;
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseLeftButtonDown - 临时关闭Popup");
#endif
            }

            // 通过 Command 调用（符合 MVVM 架构，与其他命令保持一致）
            if (ViewModel.CopyErrorInfoCommand.CanExecute(task))
            {
                await ViewModel.CopyErrorInfoCommand.ExecuteAsync(task);
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseLeftButtonDown - 已调用 ViewModel 复制命令");
#endif
            }

            // 延迟后检查鼠标位置，如果还在ErrorTag上则重新打开Popup
            await Task.Delay(100);
            if (element.IsMouseOver && popup != null)
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseLeftButtonDown - 鼠标仍在ErrorTag上，重新打开Popup");
#endif
                StopCloseTimer();
                popup.IsOpen = true;
            }
        }

        // 标记事件已处理，防止其他事件触发
        e.Handled = true;
    }

    /// <summary>
    /// 鼠标进入错误标签时打开Popup
    /// </summary>
    private void ErrorTag_MouseEnter(object sender, MouseEventArgs e)
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseEnter - 鼠标移入错误标签");
#endif

        if (sender is FrameworkElement element)
        {
            var popup = FindPopupForElement(element);

            if (popup != null)
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseEnter - 找到Popup，准备打开");
#endif
                // 如果有待关闭的计时器，取消它（处理快速移入移出的情况）
                StopCloseTimer();
                popup.IsOpen = true;
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseEnter - Popup.IsOpen = {popup.IsOpen}");
#endif
            }
            else
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseEnter - 未找到Popup");
#endif
            }
        }
    }

    /// <summary>
    /// 鼠标离开错误标签时启动延迟关闭（如果Popup已打开）
    /// </summary>
    private void ErrorTag_MouseLeave(object sender, MouseEventArgs e)
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseLeave - 鼠标移出错误标签");
#endif

        if (sender is FrameworkElement element)
        {
            var popup = FindPopupForElement(element);

            if (popup != null && popup.IsOpen)
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseLeave - Popup已打开，启动关闭计时器");
#endif
                StartCloseTimer(popup);
            }
            else if (popup == null)
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseLeave - 未找到Popup");
#endif
            }
            else
            {
#if DEBUG
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorTag_MouseLeave - Popup未打开，不需要启动计时器");
#endif
            }
        }
    }

    /// <summary>
    /// 鼠标进入Popup时取消关闭计时
    /// </summary>
    private void ErrorPopup_MouseEnter(object sender, MouseEventArgs e)
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorPopup_MouseEnter - 鼠标移入悬浮窗");
#endif
        StopCloseTimer();
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorPopup_MouseEnter - 已取消关闭计时器");
#endif
    }

    /// <summary>
    /// 鼠标离开Popup时启动延迟关闭
    /// </summary>
    private void ErrorPopup_MouseLeave(object sender, MouseEventArgs e)
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorPopup_MouseLeave - 鼠标移出悬浮窗");
#endif

        if (sender is Popup popup && popup.IsOpen)
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorPopup_MouseLeave - Popup已打开，启动关闭计时器");
#endif
            StartCloseTimer(popup);
        }
        else if (sender is not Popup)
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorPopup_MouseLeave - sender不是Popup");
#endif
        }
        else
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ErrorPopup_MouseLeave - Popup未打开");
#endif
        }
    }

    #endregion

    #region Timer Management

    /// <summary>
    /// 启动延迟关闭计时器
    /// </summary>
    private void StartCloseTimer(Popup popup)
    {
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] StartCloseTimer - 开始启动关闭计时器");
#endif
        StopCloseTimer();

        _popupCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(POPUP_CLOSE_DELAY_MS)
        };

        _popupCloseTimer.Tick += (s, e) =>
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Timer.Tick - 计时器到期，准备关闭Popup");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Timer.Tick - Popup.IsOpen (关闭前) = {popup.IsOpen}");
#endif
            popup.IsOpen = false;
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Timer.Tick - Popup.IsOpen (关闭后) = {popup.IsOpen}");
#endif
            StopCloseTimer();
        };

        _popupCloseTimer.Start();
#if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] StartCloseTimer - 计时器已启动，延迟 {POPUP_CLOSE_DELAY_MS}ms");
#endif
    }

    /// <summary>
    /// 停止延迟关闭计时器
    /// </summary>
    private void StopCloseTimer()
    {
        if (_popupCloseTimer != null)
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] StopCloseTimer - 停止并清除计时器");
#endif
            _popupCloseTimer.Stop();
            _popupCloseTimer = null;
        }
        else
        {
#if DEBUG
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] StopCloseTimer - 计时器为null，无需停止");
#endif
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// 查找与ErrorTag关联的Popup
    /// </summary>
    private Popup? FindPopupForElement(DependencyObject element)
    {
        // 向上查找包含 ErrorPopup 的根 Grid
        DependencyObject? current = element;
        Popup? popup = null;

        while (current != null && popup == null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is Grid grid)
            {
                // 尝试在当前 Grid 中查找 ErrorPopup
                popup = FindVisualChild<Popup>(grid, "ErrorPopup");
            }
        }

        return popup;
    }

    /// <summary>
    /// 向上查找可视树中的父元素
    /// </summary>
    private T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = child;

        while (current != null)
        {
            if (current is T parent)
                return parent;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 查找可视树中的子元素
    /// </summary>
    private T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent == null)
            return null;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T element && element.Name == name)
                return element;

            var result = FindVisualChild<T>(child, name);
            if (result != null)
                return result;
        }

        return null;
    }

    /// <summary>
    /// 查找可视树中所有指定类型的子元素
    /// </summary>
    private IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
            yield break;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T typedChild)
                yield return typedChild;

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// 检查点击的元素是否在 Popup 内部
    /// </summary>
    private static bool IsElementInsidePopup(DependencyObject element, Popup popup)
    {
        if (popup.Child == null)
            return false;

        // 检查元素是否是 Popup.Child 的后代
        DependencyObject? current = element;
        while (current != null)
        {
            if (current == popup.Child)
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>
    /// 检查 ancestor 是否是 descendant 的祖先元素
    /// </summary>
    private static bool IsVisualAncestorOf(DependencyObject ancestor, DependencyObject descendant)
    {
        DependencyObject? current = descendant;
        while (current != null)
        {
            if (current == ancestor)
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    #endregion
}
