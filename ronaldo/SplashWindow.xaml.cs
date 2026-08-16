using System.Windows;

namespace ronaldo;

/// <summary>Branded loading window shown while the main window is being prepared.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    public void SetStatus(string text) => StatusLine.Text = text;
}
