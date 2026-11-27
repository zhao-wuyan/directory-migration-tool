using MoveWithSymlinkWPF.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MoveWithSymlinkWPF.Views;

/// <summary>
/// UserGuideWindow.xaml 的交互逻辑
/// </summary>
public partial class UserGuideWindow : Window
{
    private readonly Button[] _navButtons;
    private readonly UIElement[] _sections;
    private int _currentSectionIndex;

    public UserGuideWindow()
    {
        InitializeComponent();

        _navButtons =
        [
            NavButton0,
            NavButton1,
            NavButton2,
            NavButton3,
            NavButton4,
            NavButton5,
            NavButton6,
            NavButton7
        ];

        _sections =
        [
            Section0,
            Section1,
            Section2,
            Section3,
            Section4,
            Section5,
            Section6,
            Section7
        ];

        string version = VersionService.GetVersion();
        VersionBadgeText.Text = version;
        FooterVersionText.Text = $"© 2025–2026 诏无言 · 目录迁移工具 · {version}";

        SelectSection(0);
    }

    private void SelectSection(int index)
    {
        if (index < 0 || index >= _sections.Length)
        {
            return;
        }

        _currentSectionIndex = index;

        for (int i = 0; i < _sections.Length; i++)
        {
            _sections[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            _navButtons[i].Tag = i == index ? "Active" : string.Empty;
        }

        NavProgressText.Text = $"第 {index + 1} / {_sections.Length} 节";
        PrevSectionButton.IsEnabled = index > 0;
        NextSectionButton.IsEnabled = index < _sections.Length - 1;
        ContentScrollViewer.ScrollToTop();
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string raw } && int.TryParse(raw, out int index))
        {
            SelectSection(index);
        }
    }

    private void PrevSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSectionIndex > 0)
        {
            SelectSection(_currentSectionIndex - 1);
        }
    }

    private void NextSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSectionIndex < _sections.Length - 1)
        {
            SelectSection(_currentSectionIndex + 1);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
