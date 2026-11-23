using System.Windows;
using System.Windows.Controls;
using ServiceDesk.Wpf.ViewModels;

namespace ServiceDesk.Wpf.Views;

public partial class SubmitTicketView : UserControl
{
    public SubmitTicketView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SubmitTicketViewModel { Categories.Count: 0 } vm && vm.LoadCategoriesCommand.CanExecute(null))
            vm.LoadCategoriesCommand.Execute(null);
    }
}
