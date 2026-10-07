using System.Windows;
using KeyNexus.Themes;

namespace KeyNexus;

public partial class TextPromptWindow : Window
{
    private string? _result;

    private TextPromptWindow()
    {
        InitializeComponent();
        WindowEffects.Apply(this, useMica: false);
        Loaded += (_, _) =>
        {
            txtValue.Focus();
            txtValue.SelectAll();
        };
    }

    /// <summary>
    /// Retorna null se cancelou, string vazia se pediu para limpar, ou o texto digitado.
    /// </summary>
    public static string? Prompt(Window owner, string heading, string message, string initialValue,
        string placeholder, bool allowClear)
    {
        var dialog = new TextPromptWindow { Owner = owner };
        dialog.txtHeading.Text = heading;
        dialog.txtMessage.Text = message;
        dialog.txtValue.Text = initialValue;
        dialog.txtValue.Tag = placeholder;
        dialog.btnClear.Visibility = allowClear ? Visibility.Visible : Visibility.Collapsed;

        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _result = txtValue.Text.Trim();
        DialogResult = true;
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _result = string.Empty;
        DialogResult = true;
    }
}
