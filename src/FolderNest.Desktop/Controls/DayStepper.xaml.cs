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
    public void ResetInput()
    {
        var binding=Editor.GetBindingExpression(TextBox.TextProperty)!;
        Validation.ClearInvalid(binding);binding.UpdateTarget();
    }
    public bool CommitInput()
    {
        var binding=Editor.GetBindingExpression(TextBox.TextProperty)!;
        var validation=new IntegerDaysRule().Validate(Editor.Text,CultureInfo.CurrentCulture);
        if(!validation.IsValid)
        {
            Validation.MarkInvalid(binding,new ValidationError(new IntegerDaysRule(),binding,validation.ErrorContent,null));return false;
        }
        Validation.ClearInvalid(binding);
        SetCurrentValue(ValueProperty,int.Parse(Editor.Text,CultureInfo.CurrentCulture));
        // The nested editor must explicitly cross the UserControl and DataGrid binding boundary.
        GetBindingExpression(ValueProperty)?.UpdateSource();
        return !Validation.GetHasError(this);
    }
    private void OnLostFocus(object sender,KeyboardFocusChangedEventArgs e)=>CommitInput();
    private void Step(int delta)
    {
        if(!CommitInput())return;
        SetCurrentValue(ValueProperty,Math.Clamp(Value+delta,1,36500));
        GetBindingExpression(ValueProperty)?.UpdateSource();
    }
    private void Increase(object sender,RoutedEventArgs e) => Step(1);
    private void Decrease(object sender,RoutedEventArgs e) => Step(-1);
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is Key.Up or Key.Down) { Step(e.Key==Key.Up ? 1 : -1); e.Handled=true; }
        else if(e.Key==Key.Enter){CommitInput();e.Handled=true;}
    }
}
