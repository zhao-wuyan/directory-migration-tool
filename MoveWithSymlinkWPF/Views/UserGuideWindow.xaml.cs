using System.Windows;

namespace MoveWithSymlinkWPF.Views
{
    /// <summary>
    /// UserGuideWindow.xaml 的交互逻辑
    /// </summary>
    public partial class UserGuideWindow : Window
    {
        public UserGuideWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
