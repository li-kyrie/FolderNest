using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FolderNest.Desktop;

public static class RubberBandSelection
{
    public static void Attach(DataGrid grid)
    {
        Point? origin=null; SelectionAdorner? adorner=null; object[] baseline=[];
        grid.PreviewMouseLeftButtonDown+=(_,e)=>
        {
            if(FindAncestor<Control>(e.OriginalSource as DependencyObject) is TextBox or ComboBox or Button) return;
            if(FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not null) return;
            if(FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null ||
                FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) is not null) return;
            origin=e.GetPosition(grid); baseline=Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? grid.SelectedItems.Cast<object>().ToArray() : [];
        };
        grid.PreviewMouseMove+=(_,e)=>
        {
            if(origin is not { } start || e.LeftButton!=MouseButtonState.Pressed) return;
            var point=e.GetPosition(grid); var rect=new Rect(start,point);
            if(rect.Width<4 && rect.Height<4) return;
            if(adorner is null){adorner=new(grid);AdornerLayer.GetAdornerLayer(grid)?.Add(adorner);grid.CaptureMouse();}
            adorner.Rectangle=rect; adorner.InvalidateVisual();
            grid.SelectedItems.Clear(); foreach(var item in baseline) grid.SelectedItems.Add(item);
            foreach(var item in grid.Items)
                if(grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row &&
                   rect.IntersectsWith(new Rect(row.TranslatePoint(new Point(),grid),new Size(row.ActualWidth,row.ActualHeight))) &&
                   !grid.SelectedItems.Contains(item)) grid.SelectedItems.Add(item);
            e.Handled=true;
        };
        grid.PreviewMouseLeftButtonUp+=(_,_)=>
        {
            origin=null; if(adorner is not null){AdornerLayer.GetAdornerLayer(grid)?.Remove(adorner);adorner=null;grid.ReleaseMouseCapture();}
        };
    }
    private static T? FindAncestor<T>(DependencyObject? source) where T:DependencyObject
    {
        while(source is not null){if(source is T target)return target;source=source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);}
        return null;
    }
    private sealed class SelectionAdorner(UIElement element) : Adorner(element)
    {
        public Rect Rectangle { get; set; }
        protected override void OnRender(DrawingContext drawingContext)
        { IsHitTestVisible=false; drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(40,38,91,120)),new Pen(Brushes.SteelBlue,1),Rectangle); }
    }
}
