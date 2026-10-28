#nullable enable
using Avalonia.Controls;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    public partial class ServerListView : UserControl
    {
        public ServerListViewModel? ViewModel => DataContext as ServerListViewModel;

        public ServerListView()
        {
            InitializeComponent();
        }
    }
}
