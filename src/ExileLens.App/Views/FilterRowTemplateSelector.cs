using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ExileLens.App.ViewModels;

namespace ExileLens.App.Views;

/// <summary>
/// Picks the modifier row layout once per row: single-line affixes keep the inline "tag + text" checkbox,
/// multi-line affixes get the stacked layout. Only the chosen tree is built (no hidden twin bound per row).
/// </summary>
public sealed class FilterRowTemplateSelector : IDataTemplate
{
    public IDataTemplate? SingleLine { get; set; }

    public IDataTemplate? MultiLine { get; set; }

    public Control? Build(object? param) =>
        (param is FilterItemViewModel { HasExtraLines: true } ? MultiLine : SingleLine)?.Build(param);

    public bool Match(object? data) => data is FilterItemViewModel;
}
