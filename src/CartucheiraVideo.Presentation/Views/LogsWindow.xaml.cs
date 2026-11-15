using System.Collections.Specialized;
using System.Windows;
using CartucheiraVideo.Presentation.ViewModels;

namespace CartucheiraVideo.Presentation.Views;

/// <summary>
/// Fix v1.0.75 (2026-11-13): ver comentário completo em <see cref="MainViewModel.OpenLogsCommand"/>
/// (<c>MainViewModel.OpenLogs</c>) e no XAML desta janela. Janela puramente de leitura — não tem
/// nenhuma lógica própria além de manter a lista rolada até a última linha conforme
/// <see cref="MainViewModel.StatusHistory"/> recebe entradas novas em tempo real.
/// </summary>
public partial class LogsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public LogsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.StatusHistory.CollectionChanged += OnStatusHistoryChanged;
        Closed += (_, _) => _viewModel.StatusHistory.CollectionChanged -= OnStatusHistoryChanged;

        ScrollToEnd();
    }

    private void OnStatusHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    private void ScrollToEnd()
    {
        if (HistoryListBox.Items.Count > 0)
        {
            HistoryListBox.ScrollIntoView(HistoryListBox.Items[^1]);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Fix v1.0.81 (2026-11-15): novo, junto com a releitura visual da janela (ver comentário
    /// completo no XAML) — copia o histórico inteiro (uma linha por entrada de
    /// <see cref="MainViewModel.StatusHistory"/>) pra área de transferência, pra colar direto numa
    /// conversa de suporte em vez de print/retranscrever à mão. `Clipboard.SetText` lança se a área
    /// de transferência estiver ocupada por outro processo no instante exato (falha transitória
    /// documentada do Windows) — nunca derruba a janela de Logs por causa disso, só avisa.
    /// </summary>
    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _viewModel.StatusHistory));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível copiar pra área de transferência: {ex.Message}", "Cartucheira Vídeo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
