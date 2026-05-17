using System;
using Avalonia.Controls;
using IlliciumTTY.ViewModels;

namespace IlliciumTTY.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Shutdown();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closing -= OnClosing;
        Closed -= OnClosed;
    }
}