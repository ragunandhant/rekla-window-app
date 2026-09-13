using System.Windows;
using System.Windows.Controls;

namespace RaceVideoProcessor.App.Views;

public enum ChoiceStyle
{
    Secondary,
    Primary,
    Danger
}

/// <param name="Result">Returned when the button is clicked.</param>
/// <param name="IsCancel">Also chosen by Escape or by closing the window.</param>
public sealed record Choice(string Label, string Result, ChoiceStyle Style = ChoiceStyle.Secondary, bool IsCancel = false);

public partial class ChoiceDialog : Window
{
    private string _result;

    private ChoiceDialog(string title, string heading, string body, string? warning, IReadOnlyList<Choice> choices)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = heading;
        BodyText.Text = body;
        if (!string.IsNullOrWhiteSpace(warning))
        {
            WarningText.Text = warning;
            WarningText.Visibility = Visibility.Visible;
        }

        _result = choices.FirstOrDefault(c => c.IsCancel)?.Result ?? string.Empty;

        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice.Label,
                IsCancel = choice.IsCancel,
                Margin = new Thickness(10, 0, 0, 0),
                Style = (Style)FindResource(choice.Style switch
                {
                    ChoiceStyle.Primary => "Btn.Primary",
                    ChoiceStyle.Danger => "Btn.Danger",
                    _ => "Btn.Secondary"
                })
            };
            button.Click += (_, _) =>
            {
                _result = choice.Result;
                Close();
            };
            Buttons.Children.Add(button);

            // The safe choice has focus, so Enter never triggers a destructive action.
            if (choice.IsCancel)
                Loaded += (_, _) => button.Focus();
        }
    }

    /// <summary>Shows the dialog and returns the chosen <see cref="Choice.Result"/>.</summary>
    public static string Show(string title, string heading, string body, string? warning, params Choice[] choices)
    {
        var dialog = new ChoiceDialog(title, heading, body, warning, choices)
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        return dialog._result;
    }
}
