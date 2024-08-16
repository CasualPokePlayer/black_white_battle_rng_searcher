using System;

using Avalonia;

namespace Program;

internal static class Program
{
	public static AppBuilder BuildAvaloniaApp() 
		=> AppBuilder.Configure<App>().UsePlatformDetect();

	[STAThread]
	private static void Main(string[] args)
		=> BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
