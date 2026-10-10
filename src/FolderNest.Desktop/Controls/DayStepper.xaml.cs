using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FolderNest.Desktop.Controls;

public sealed class IntegerDaysRule : ValidationRule
{
    public override ValidationResult Validate(object value,CultureInfo cultureInfo) =>
        int.TryParse(value?.ToString(),out var n) && n is >=1 and <=36500 ? ValidationResult.ValidResult :
        new(false,"天数必须是 1–36500 的整数。");
}
public partial class DayStepper : UserControl
{
    public static readonly DependencyProperty ValueProperty=DependencyProperty.Register(nameof(Value),typeof(int),
        typeof(DayStepper),new FrameworkPropertyMetadata(30,FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty,value); }
    public DayStepper() => InitializeComponent();
    private void Step(int delta) => SetCurrentValue(ValueProperty,Math.Clamp(Value+delta,1,36500));
    private void Increase(object sender,RoutedEventArgs e) => Step(1);
    private void Decrease(object sender,RoutedEventArgs e) => Step(-1);
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is Key.Up or Key.Down) { Step(e.Key==Key.Up ? 1 : -1); e.Handled=true; }
    }
}
