using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using RevitPlanningPlugin.UI.ViewModels;

namespace RevitPlanningPlugin.UI.Views
{
    public partial class MainWindow : Window
    {
        private MainViewModel _viewModel = null!;

        public MainWindow(ExternalCommandData commandData)
        {
            InitializeComponent();
            _viewModel = new MainViewModel(commandData);
            DataContext = _viewModel;

            // Инициализируем PasswordBox из сохранённых настроек (DPAPI-расшифрованные)
            ApiKeyBox.Password      = _viewModel.Settings.ApiKey;
            BearerTokenBox.Password = _viewModel.Settings.BearerToken;

            Closed += (_, __) => _viewModel?.Dispose();
        }

        /// <summary>
        /// PasswordBox не поддерживает двухстороннее binding — обновляем ViewModel напрямую.
        /// </summary>
        private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.Settings != null)
                _viewModel.Settings.ApiKey = ApiKeyBox.Password;
        }

        private void BearerTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.Settings != null)
                _viewModel.Settings.BearerToken = BearerTokenBox.Password;
        }
    }
}
