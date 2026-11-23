using System.Windows;
using System.Windows.Controls;

namespace RemoteConnectionManager.App.Views;

/// <summary>Asks for one line of text, with a check that explains what is wrong before OK is allowed.</summary>
public partial class PromptWindow : Window
{
    private readonly Func<string, string?>? _validate;

    public string Value => ValueBox.Text;

    public PromptWindow(string title, string message, string initial, Func<string, string?>? validate, string okLabel)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        OkButton.Content = okLabel;
        _validate = validate;
        ValueBox.Text = initial;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            // Select the name without its extension, as Explorer does on rename.
            var dot = initial.LastIndexOf('.');
            ValueBox.Select(0, dot > 0 ? dot : initial.Length);
            Check(showProblem: false);
        };
    }

    private bool Check(bool showProblem)
    {
        var problem = _validate?.Invoke(ValueBox.Text);
        OkButton.IsEnabled = problem is null;
        ProblemText.Text = problem ?? string.Empty;
        ProblemText.Visibility = problem is not null && (showProblem || ValueBox.Text.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        return problem is null;
    }

    private void ValueBox_TextChanged(object sender, TextChangedEventArgs e) => Check(showProblem: true);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Check(showProblem: true)) DialogResult = true;
    }
}
