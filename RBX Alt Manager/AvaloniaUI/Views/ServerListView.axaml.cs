using Avalonia.Controls;
using RBX_Alt_Manager.ViewModels;

namespace RBX_Alt_Manager.AvaloniaUI.Views
{
    public partial class ServerListView : UserControl
    {
        public ServerListViewModel ViewModel => DataContext as ServerListViewModel;

        public ServerListView()
        {
            InitializeComponent();
        }
    }
}
