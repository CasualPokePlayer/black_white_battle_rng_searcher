using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Program;

public partial class MainWindow : Window
{
	private readonly MainViewModel _mainViewModel;

	public MainWindow()
	{
		InitializeComponent();
		_mainViewModel = new();
		DataContext = _mainViewModel;
	}

	public void ComputeSeedsHandler(object sender, RoutedEventArgs args)
	{
		_mainViewModel.ComputeSeeds();
	}

	public void AddMoveHandler(object sender, RoutedEventArgs args)
	{
		_mainViewModel.AddMove();
	}

	public void ResetHandler(object sender, RoutedEventArgs args)
	{
		_mainViewModel.Reset();
	}
}
